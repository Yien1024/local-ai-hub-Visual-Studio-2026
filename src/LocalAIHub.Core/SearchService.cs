using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 联网搜索：Bing（cn 节点）→ 360 → 百度 回退链。
    /// 每个引擎先用自己的解析器，失败退回"标题 + 外链"通用解析器，
    /// 所以某个引擎改版是降级而不是整体失效。
    /// 移植自 C:\Users\zx\mcp\web-search-mcp.py（已在本机实测可用）。
    /// </summary>
    public sealed class SearchService
    {
        // 尊重引号属性的标签正则（属性值里可能有 ">"）
        private static readonly Regex TagRe = new Regex(
            "<[^>\"']*(?:\"[^\"]*\"[^>\"']*|'[^']*'[^>\"']*)*>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ScriptStyleRe = new Regex(
            @"(?is)<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.Compiled);

        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly HttpFetcher _http;
        private readonly Dictionary<string, DateTime> _lastCall = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, SearchResponse> _cache = new Dictionary<string, SearchResponse>();
        private readonly object _gate = new object();
        private const double MinEngineIntervalSeconds = 1.0;
        private const int CacheTtlSeconds = 300;
        private const int CacheMaxEntries = 64;

        public SearchService(HttpFetcher http)
        {
            _http = http;
        }

        public static string StripTags(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return "";
            var s = ScriptStyleRe.Replace(fragment, " ");
            s = TagRe.Replace(s, " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            return WsRe.Replace(s, " ").Trim();
        }

        /// <summary>Bing 把结果包在 /ck/a?...&amp;u=a1&lt;base64url&gt; 里，要解开。</summary>
        public static string DecodeBingLink(string href)
        {
            if (string.IsNullOrEmpty(href) || href.IndexOf("bing.com/ck/a", StringComparison.OrdinalIgnoreCase) < 0)
                return href;
            try
            {
                var idx = href.IndexOf('?');
                if (idx < 0) return href;
                var query = href.Substring(idx + 1);
                string token = null;
                foreach (var pair in query.Split('&'))
                {
                    var kv = pair.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && kv[0] == "u")
                    {
                        token = Uri.UnescapeDataString(kv[1]);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(token)) return href;
                if (token.StartsWith("a1", StringComparison.Ordinal)) token = token.Substring(2);
                if (token.Length == 0) return href;
                token = token.Replace('-', '+').Replace('_', '/');
                switch (token.Length % 4)
                {
                    case 2: token += "=="; break;
                    case 3: token += "="; break;
                }
                var bytes = Convert.FromBase64String(token);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return href;
            }
        }

        /// <summary>引擎无关的兜底解析：找"标题里带外链"的 h2/h3，不依赖 class 名。</summary>
        public static List<SearchResult> SearchGeneric(string page, string selfHost, int limit)
        {
            var results = new List<SearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var re = new Regex("(?is)<h[23][^>]*>\\s*<a[^>]+href=\"([^\"]+)\"[^>]*>(.*?)</a>");
            foreach (Match m in re.Matches(page))
            {
                var link = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                var title = StripTags(m.Groups[2].Value);
                if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (title.Length < 4) continue;
                if (!seen.Add(link)) continue;
                if (HostOf(link).IndexOf(selfHost, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var tailStart = m.Index + m.Length;
                var tailLen = Math.Min(1200, Math.Max(0, page.Length - tailStart));
                var tail = tailLen > 0 ? page.Substring(tailStart, tailLen) : "";
                tail = Regex.Split(tail, "(?is)<h[23]")[0];
                results.Add(new SearchResult
                {
                    Title = title,
                    Url = link,
                    Snippet = Truncate(StripTags(tail), 240),
                });
                if (results.Count >= limit) break;
            }
            return results;
        }

        public static List<SearchResult> ParseBing(string page, int limit)
        {
            var results = new List<SearchResult>();
            var blocks = Regex.Split(page, "(?i)<li class=\"b_algo\"[^>]*>");
            for (var i = 1; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var m = Regex.Match(block, "(?is)<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>");
                if (!m.Success) continue;
                var link = DecodeBingLink(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                var title = StripTags(m.Groups[2].Value);
                var sm = Regex.Match(block, "(?is)<p[^>]*>(.*?)</p>");
                var snippet = sm.Success ? StripTags(sm.Groups[1].Value) : Truncate(StripTags(block), 240);
                if (!string.IsNullOrEmpty(title) && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    results.Add(new SearchResult { Title = title, Url = link, Snippet = snippet });
                if (results.Count >= limit) break;
            }
            return results.Count > 0 ? results : SearchGeneric(page, "bing.com", limit);
        }

        public static List<SearchResult> Parse360(string page, int limit)
        {
            var results = new List<SearchResult>();
            var blocks = Regex.Split(page, "(?i)<li[^>]+class=\"res-list\"[^>]*>");
            for (var i = 1; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var m = Regex.Match(block, "(?is)<h3[^>]*>\\s*<a[^>]*>(.*?)</a>");
                if (!m.Success) continue;
                var title = StripTags(m.Groups[1].Value);
                var real = Regex.Match(block, "\\sdata-mdurl=\"([^\"]+)\"");
                var href = Regex.Match(block, "href=\"([^\"]+)\"");
                var link = real.Success ? real.Groups[1].Value : (href.Success ? href.Groups[1].Value : "");
                link = System.Net.WebUtility.HtmlDecode(link);
                var sm = Regex.Match(block, "(?is)<p[^>]+class=\"res-desc[^\"]*\"[^>]*>(.*?)</p>");
                var snippet = sm.Success
                    ? StripTags(sm.Groups[1].Value)
                    : Truncate(StripTags(Regex.Replace(block, "(?is)<h3.*?</h3>", " ")), 240);
                if (!string.IsNullOrEmpty(title) && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    results.Add(new SearchResult { Title = title, Url = link, Snippet = snippet });
                if (results.Count >= limit) break;
            }
            return results.Count > 0 ? results : SearchGeneric(page, "so.com", limit);
        }

        public static List<SearchResult> ParseBaidu(string page, int limit)
        {
            var results = new List<SearchResult>();
            // 只有 class="result"（后面不是 - 或字母）才是真结果；result-molecule 是 AI 卡片
            var starts = Regex.Matches(page, "(?i)<div[^>]+class=\"result(?![-\\w])[^\"]*\"[^>]*>");
            for (var i = 0; i < starts.Count; i++)
            {
                var startTag = starts[i].Value;
                var from = starts[i].Index + starts[i].Length;
                var to = (i + 1 < starts.Count) ? starts[i + 1].Index : page.Length;
                if (to <= from) continue;
                var block = page.Substring(from, to - from);
                var m = Regex.Match(block, "(?is)<h3[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>");
                if (!m.Success) continue;
                var title = StripTags(m.Groups[2].Value);
                var real = Regex.Match(startTag, "\\smu=\"([^\"]+)\"");
                if (!real.Success) real = Regex.Match(block, "\\smu=\"([^\"]+)\"");
                var link = System.Net.WebUtility.HtmlDecode(real.Success ? real.Groups[1].Value : m.Groups[1].Value);
                var sm = Regex.Match(block, "(?is)<div[^>]+class=\"c-abstract[^\"]*\"[^>]*>(.*?)</div>");
                if (!sm.Success) sm = Regex.Match(block, "(?is)<span[^>]+class=\"content-right[^\"]*\"[^>]*>(.*?)</span>");
                var snippet = sm.Success
                    ? StripTags(sm.Groups[1].Value)
                    : Truncate(StripTags(Regex.Replace(block, "(?is)<h3.*?</h3>", " ")), 240);
                if (!string.IsNullOrEmpty(title) && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    results.Add(new SearchResult { Title = title, Url = link, Snippet = snippet });
                if (results.Count >= limit) break;
            }
            return results.Count > 0 ? results : SearchGeneric(page, "baidu.com", limit);
        }

        /// <summary>搜索引擎链：Bing → 360 → 百度。engine 传 "bing"/"360"/"baidu" 可指定。</summary>
        public async Task<SearchResponse> SearchAsync(string query, int limit = 8, string engine = "auto",
            CancellationToken ct = default(CancellationToken))
        {
            query = (query ?? "").Trim();
            limit = Math.Max(1, Math.Min(limit, 20));
            if (query.Length == 0)
                return new SearchResponse { Query = query, Engine = "none", Error = "empty query" };

            var cacheKey = query.ToLowerInvariant() + "|" + limit + "|" + engine;
            lock (_gate)
            {
                SearchResponse hit;
                if (_cache.TryGetValue(cacheKey, out hit)) return hit;
            }

            var order = (engine == "auto" || string.IsNullOrEmpty(engine))
                ? new[] { "bing", "360", "baidu" }
                : new[] { engine };
            var notes = new List<string>();
            foreach (var eng in order)
            {
                try
                {
                    await PaceAsync(eng, ct).ConfigureAwait(false);
                    List<SearchResult> found;
                    switch (eng)
                    {
                        case "bing":
                            found = ParseBing(await _http.GetStringAsync(
                                "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query)
                                + "&setlang=zh-CN&count=30", 20, 4_000_000, ct).ConfigureAwait(false), limit);
                            break;
                        case "360":
                            found = Parse360(await _http.GetStringAsync(
                                "https://www.so.com/s?q=" + Uri.EscapeDataString(query),
                                20, 4_000_000, ct).ConfigureAwait(false), limit);
                            break;
                        case "baidu":
                            found = ParseBaidu(await _http.GetStringAsync(
                                "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(query) + "&rn=20",
                                20, 4_000_000, ct).ConfigureAwait(false), limit);
                            break;
                        default:
                            notes.Add("unknown engine: " + eng);
                            continue;
                    }
                    if (found.Count > 0)
                    {
                        var resp = new SearchResponse
                        {
                            Query = query,
                            Engine = eng,
                            RawCount = found.Count,
                            Results = found,
                        };
                        lock (_gate) { CachePut(cacheKey, resp); }
                        return resp;
                    }
                    notes.Add(eng + ": 0 results");
                }
                catch (Exception ex)
                {
                    notes.Add(eng + ": " + ex.GetType().Name + " " + ex.Message);
                    Log.Info("search engine " + eng + " failed: " + ex.Message);
                }
            }
            return new SearchResponse
            {
                Query = query,
                Engine = "none",
                RawCount = 0,
                Error = string.Join("; ", notes),
            };
        }

        private async Task PaceAsync(string engine, CancellationToken ct)
        {
            double wait;
            lock (_gate)
            {
                DateTime last;
                if (_lastCall.TryGetValue(engine, out last))
                    wait = MinEngineIntervalSeconds - (DateTime.UtcNow - last).TotalSeconds;
                else wait = 0;
                _lastCall[engine] = DateTime.UtcNow;
            }
            if (wait > 0)
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
        }

        private void CachePut(string key, SearchResponse value)
        {
            if (_cache.Count >= CacheMaxEntries)
            {
                var now = DateTime.UtcNow;
                foreach (var k in _cache.Keys.ToList()) _cache.Remove(k);
            }
            _cache[key] = value;
        }

        /// <summary>
        /// 抓一个网页正文。要点：跳过首页（首页很少有答案）；硬超时；
        /// 只保留命中检索词的段落（正文里导航/广告占比极高）。
        /// </summary>
        public async Task<FetchedPage> FetchPageAsync(string url, string query, int maxChars = 2500,
            int deadlineSeconds = 8, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new FetchedPage { Url = url, Error = "not a content url" };

            var task = FetchCoreAsync(url, query, maxChars, ct);
            var result = await Deadline.RunAsync(task, deadlineSeconds, null).ConfigureAwait(false);
            return result ?? new FetchedPage { Url = url, Error = "deadline/failure" };
        }

        private async Task<FetchedPage> FetchCoreAsync(string url, string query, int maxChars,
            CancellationToken ct)
        {
            var html = await _http.GetStringAsync(url, 15, 2_000_000, ct).ConfigureAwait(false);
            var title = "";
            var tm = Regex.Match(html, "(?is)<title[^>]*>(.*?)</title>");
            if (tm.Success) title = StripTags(tm.Groups[1].Value);

            var body = ScriptStyleRe.Replace(html, " ");
            // 按块切分，保留命中检索词的段落（前后各 1 段上下文）
            var chunks = Regex.Split(body, "(?is)</(?:p|div|li|h[1-6]|tr|br)>");
            var terms = QueryTerms(query);
            var keep = new List<string>();
            for (var i = 0; i < chunks.Length; i++)
            {
                var text = StripTags(chunks[i]);
                if (text.Length < 30) continue;
                if (terms.Count == 0 || terms.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (i > 0) { var prev = StripTags(chunks[i - 1]); if (prev.Length >= 30) keep.Add(prev); }
                    keep.Add(text);
                    if (i + 1 < chunks.Length)
                    {
                        var next = StripTags(chunks[i + 1]);
                        if (next.Length >= 30) keep.Add(next);
                    }
                }
                if (string.Join(" ", keep).Length > maxChars * 2) break;
            }
            var assembled = keep.Count > 0 ? string.Join("\n", keep) : StripTags(body);
            return new FetchedPage
            {
                Url = url,
                Title = title,
                Text = Truncate(assembled, maxChars),
                Truncated = assembled.Length > maxChars,
            };
        }

        public static string HostOf(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return ""; }
        }

        public static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        /// <summary>英文词（>=3 字符）+ 中文 2-gram，用于粗略相关度打分。</summary>
        public static HashSet<string> QueryTerms(string q)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return terms;
            foreach (Match m in Regex.Matches(q.ToLowerInvariant(), "[a-z][a-z0-9_.+-]{2,}"))
            {
                if (terms.Add(m.Value) && m.Value.Length > MaxTermLength)
                    MaxTermLength = m.Value.Length;
            }
            var han = Regex.Matches(q, "[\\u4e00-\\u9fff]").Cast<Match>().Select(m => m.Value[0]).ToList();
            for (var i = 0; i + 1 < han.Count; i++)
                terms.Add(new string(new[] { han[i], han[i + 1] }));
            return terms;
        }

        /// <summary>见过的英文检索词最大长度（诊断用：长标识符会拉低整体重合度）。</summary>
        public static int MaxTermLength { get; private set; }
    }
}
