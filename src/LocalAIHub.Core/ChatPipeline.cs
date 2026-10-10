using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 主链路：判定要不要联网 → 抽检索词 → 搜索 → 相关性闸门 → 抓正文 → 注入 → 流式回答。
    /// 这是整个插件的核心，也是唯一需要"讲清楚为什么这么设计"的地方。
    /// </summary>
    public sealed class ChatPipeline : IDisposable
    {
        private readonly HttpFetcher _http;
        private readonly SearchService _search;
        private readonly Dictionary<string, string> _blockCache = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> _blockCacheAt = new Dictionary<string, DateTime>();
        private readonly object _gate = new object();
        private const int BlockTtlSeconds = 300;
        private const int BlockCacheMax = 32;

        public HubSettings Settings { get; private set; }
        public OllamaClient Ollama { get; private set; }

        public ChatPipeline(HubSettings settings)
        {
            Settings = settings ?? new HubSettings();
            _http = new HttpFetcher();
            _search = new SearchService(_http);
            Ollama = new OllamaClient(Settings.OllamaEndpoint);
        }

        public void ApplySettings(HubSettings settings)
        {
            Settings = settings ?? Settings;
            Ollama.Endpoint = Settings.OllamaEndpoint;
        }

        public void ClearSearchCache()
        {
            lock (_gate) { _blockCache.Clear(); _blockCacheAt.Clear(); }
        }

        /// <summary>是否需要联网（受设置里的 SearchMode 控制）。</summary>
        public bool DecideSearch(string userText, out string reason)
        {
            if (!Settings.WebSearchEnabled)
            {
                reason = "联网已关闭";
                return false;
            }
            var text = (userText ?? "").Trim();
            if (text.Contains("[no-search]"))
            {
                reason = "[no-search] 令牌";
                return false;
            }
            if (text.Contains("[search]"))
            {
                reason = "[search] 令牌强制联网";
                return true;
            }
            switch ((Settings.SearchMode ?? "auto").ToLowerInvariant())
            {
                case "never":
                    reason = "SearchMode=never";
                    return false;
                case "always":
                    reason = "SearchMode=always";
                    return true;
                default:
                    var need = QueryDecision.NeedsWebSearch(text);
                    reason = need ? "规则判定：需要外部信息" : "规则判定：纯代码/概念/延续，跳过";
                    return need;
            }
        }

        /// <summary>执行检索并构造注入块。返回 null 表示不注入。</summary>
        public async Task<InjectionResult> BuildInjectionAsync(string userText, CancellationToken ct)
        {
            var result = new InjectionResult();
            var sw = Stopwatch.StartNew();

            var text = (userText ?? "").Replace("[search]", "").Replace("[no-search]", "").Trim();

            string reason;
            if (!DecideSearch(userText, out reason))
            {
                result.Reason = reason;
                Log.Info("跳过联网：" + reason);
                return result;
            }

            // 缓存：agent 模式下同一句话每轮工具往返都会重发，不缓存就会重复搜
            var cacheKey = text;
            lock (_gate)
            {
                DateTime at;
                string cached;
                if (_blockCache.TryGetValue(cacheKey, out cached)
                    && _blockCacheAt.TryGetValue(cacheKey, out at)
                    && (DateTime.UtcNow - at).TotalSeconds < BlockTtlSeconds)
                {
                    result.Block = cached;
                    result.FromCache = true;
                    result.Reason = "命中检索块缓存（TTL " + BlockTtlSeconds + "s）";
                    Log.Info("检索块缓存命中");
                    return result;
                }
            }

            var query = QueryDecision.ExtractSearchQuery(text);
            result.Query = query;
            var realtime = QueryDecision.IsRealtime(text);
            var minScore = realtime ? Settings.RelevanceMinRealtime : Settings.RelevanceMin;

            var resp = await _search.SearchAsync(query, 8, "auto", ct).ConfigureAwait(false);
            result.Engine = resp.Engine;
            result.Raw = resp.RawCount;
            result.SearchSeconds = sw.Elapsed.TotalSeconds;

            List<SearchResult> kept;
            double top;
            bool weak;
            ContextBlocks.RankAndGate(resp.Results, query, minScore, Settings.MaxInjected, true,
                out kept, out top, out weak);
            result.Kept = kept.Count;
            result.Weak = weak;
            result.TopScore = top;

            Log.Info("搜索：query=\"" + query + "\" engine=" + resp.Engine + " raw=" + resp.RawCount
                     + " kept=" + kept.Count + " top=" + top.ToString("0.00")
                     + " thr=" + minScore.ToString("0.00") + " realtime=" + realtime
                     + (weak ? " WEAK" : "") + " 耗时 " + result.SearchSeconds.ToString("0.00") + "s");

            if (kept.Count == 0)
            {
                var why = resp.RawCount == 0
                    ? "搜索引擎没有返回结果；" + (resp.Error ?? "")
                    : "引擎返回 " + resp.RawCount + " 条，全部低于相关性阈值 " + minScore.ToString("0.00");
                result.Block = ContextBlocks.BuildEmptyContextBlock(query, why);
                result.Reason = "无相关结果（已告知模型搜不到）";
            }
            else
            {
                var rendered = ContextBlocks.RenderSources(kept, resp.Engine);

                // 抓 1 个正文页（硬超时）。优先抓相关度最高且看起来像内容页的
                FetchedPage fetched = null;
                var candidate = PickContentUrl(kept);
                if (candidate != null)
                {
                    var fsw = Stopwatch.StartNew();
                    fetched = await _search.FetchPageAsync(candidate.Url, query, 2500,
                        Settings.FetchDeadlineSeconds, ct).ConfigureAwait(false);
                    result.FetchSeconds = fsw.Elapsed.TotalSeconds;
                    result.Fetched = fetched != null && !string.IsNullOrEmpty(fetched.Text);
                    if (!result.Fetched)
                        Log.Info("正文抓取未成功：" + (fetched == null ? "null" : fetched.Error));
                }
                result.Block = ContextBlocks.BuildContextBlock(query, kept.Count, rendered, fetched, weak);
                result.Reason = "注入 " + kept.Count + " 条来源" + (result.Fetched ? " + 1 篇正文" : "");
            }

            lock (_gate)
            {
                if (_blockCache.Count >= BlockCacheMax) { _blockCache.Clear(); _blockCacheAt.Clear(); }
                _blockCache[cacheKey] = result.Block;
                _blockCacheAt[cacheKey] = DateTime.UtcNow;
            }
            return result;
        }

        /// <summary>跳过首页（首页很少有答案）。</summary>
        private static SearchResult PickContentUrl(List<SearchResult> kept)
        {
            foreach (var r in kept)
            {
                try
                {
                    var path = new Uri(r.Url).AbsolutePath.TrimEnd('/');
                    if (path.Length == 0) continue;
                    if (path.Equals("/home", StringComparison.OrdinalIgnoreCase)) continue;
                    return r;
                }
                catch { }
            }
            return null;
        }

        /// <summary>不联网，直接流式生成。</summary>
        public Task<GenerationStats> ChatAsync(string model, List<ChatMessage> messages,
            Action<string> onDelta, Action<string> onReasoning,
            CancellationToken ct)
        {
            return Ollama.ChatStreamAsync(model, messages, Settings.Temperature,
                Settings.ContextLength, Settings.MaxOutputTokens, Settings.KeepAliveForever,
                onDelta, onReasoning, ct);
        }

        public void Dispose()
        {
            try { _http.Dispose(); } catch { }
            try { Ollama.Dispose(); } catch { }
        }
    }

    public sealed class InjectionResult
    {
        public string Block { get; set; }
        public string Query { get; set; }
        public string Engine { get; set; }
        public int Raw { get; set; }
        public int Kept { get; set; }
        public bool Weak { get; set; }
        public double TopScore { get; set; }
        public bool Fetched { get; set; }
        public bool FromCache { get; set; }
        public double SearchSeconds { get; set; }
        public double FetchSeconds { get; set; }
        public string Reason { get; set; }
    }
}
