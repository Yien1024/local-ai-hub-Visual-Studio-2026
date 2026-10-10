using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 注入前的结果排名与相关性闸门 + 注入块的构造。
    ///
    /// 这两件事是上一轮"联网反而更不准"的根因修复：
    ///   1) 旧代码按域名打分重排，把 csdn 的《如何安装 Python》顶成"正文页"，
    ///      而问题是 requests 模块导入失败 —— 塞了无关内容还命令模型"直接引用"。
    ///   2) 现在：内容农场直接剔除 → 字面相关度打分 → 低于阈值不注入 →
    ///      一条都不相关就明确告诉模型"没搜到"，而不是硬塞垃圾。
    /// </summary>
    public static class ContextBlocks
    {
        private static readonly string[] LowQualityHosts =
        {
            "csdn.net", "jianshu.com", "51cto.com", "cnblogs.com", "360doc.com",
            "baidu.com", "so.com",
        };

        private static readonly string[] TrustedHosts =
        {
            "stackoverflow.com", "stackexchange.com", "github.com", "github.io",
            "developer.mozilla.org", "docs.python.org", "docs.oracle.com",
            "learn.microsoft.com", "react.dev", "vuejs.org", "nodejs.org",
            "rust-lang.org", "go.dev", "pkg.go.dev", "spring.io",
            "fastapi.tiangolo.com", "pytorch.org", "huggingface.co", "ollama.com",
        };

        private static bool HostMatches(string host, string[] domains)
        {
            if (string.IsNullOrEmpty(host)) return false;
            return domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>单条结果与检索词的字面重合度，0~1。</summary>
        public static double Relevance(SearchResult r, string query, HashSet<string> terms)
        {
            if (terms == null || terms.Count == 0) return 1.0;
            var blob = ((r.Title ?? "") + " " + (r.Snippet ?? "")).ToLowerInvariant();
            var hit = terms.Count(t => TermMatches(blob, t));
            return hit / (double)terms.Count;
        }

        /// <summary>
        /// 一个检索词是否出现在结果文本里。
        /// 对长标识符做"字母数字切分 + 覆盖率"匹配：
        /// 实测踩到的 bug —— 检索词里的 `modulenotfounderror`（去掉符号后是一整块）
        /// 与页面上的 `ModuleNotFoundError` 只差大小写、本该命中；但如果页面上写的是
        /// `Module Not Found Error`，整块匹配就会漏。覆盖率匹配两种情况都能命中，
        /// 同时保留 70% 的门槛，避免"只沾一点边"的结果混进来。
        /// </summary>
        internal static bool TermMatches(string lowerBlob, string term)
        {
            if (string.IsNullOrEmpty(term)) return false;
            var t = term.ToLowerInvariant();
            if (lowerBlob.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
            if (t.Length < 12) return false;

            var parts = new List<string>();
            var sb = new StringBuilder();
            foreach (var c in t)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else { if (sb.Length > 1) parts.Add(sb.ToString()); sb.Length = 0; }
            }
            if (sb.Length > 1) parts.Add(sb.ToString());
            if (parts.Count == 0) return false;

            var joined = string.Join("", parts);
            if (joined.Length >= 8 && lowerBlob.Contains(joined)) return true;

            var hit = parts.Count(p => lowerBlob.IndexOf(p, StringComparison.Ordinal) >= 0);
            return hit / (double)parts.Count >= 0.7;
        }

        /// <summary>
        /// 排名 + 闸门。返回 (通过的列表, 最高分, 是否弱证据)。
        /// neverEmpty：即使全低于阈值，也保留最高分那条并标弱——实测这条
        /// 修复了"14 条真实 query 里 5 条被完全滤空"的回归。
        /// </summary>
        public static void RankAndGate(List<SearchResult> results, string query, double minScore,
            int maxKeep, bool neverEmpty,
            out List<SearchResult> kept, out double topScore, out bool weak)
        {
            kept = new List<SearchResult>();
            topScore = 0;
            weak = false;
            if (results == null || results.Count == 0) return;

            var terms = SearchService.QueryTerms(query);
            var scored = new List<KeyValuePair<double, SearchResult>>();
            foreach (var r in results)
            {
                if (string.IsNullOrEmpty(r.Url) || !r.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                var host = SearchService.HostOf(r.Url);
                if (HostMatches(host, LowQualityHosts)) continue;   // 内容农场，直接不要
                var score = Relevance(r, query, terms);
                if (HostMatches(host, TrustedHosts)) score += 0.15;
                scored.Add(new KeyValuePair<double, SearchResult>(score, r));
            }
            if (scored.Count == 0) return;

            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            topScore = scored[0].Key;

            foreach (var pair in scored)
            {
                if (pair.Key >= minScore) kept.Add(pair.Value);
                if (kept.Count >= maxKeep) break;
            }

            if (kept.Count == 0 && neverEmpty)
            {
                // 实测教训：旧版本这里写了 topScore > 0 的前提，于是"完全零重合"的
                // 结果会被静默滤空——而那恰恰是最需要告诉用户"没搜到相关的"的场景。
                // 宁少勿滥不等于宁可不说话。
                kept.Add(scored[0].Value);
                weak = true;
            }
            else if (kept.Count > 0 && topScore < minScore)
            {
                weak = true;
            }
        }

        /// <summary>带 [S1][S2] 编号的来源列表，让"引用要标来源"这条规则可执行。</summary>
        public static string RenderSources(List<SearchResult> results, string engine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("来源列表（搜索引擎：" + (string.IsNullOrEmpty(engine) ? "?" : engine) + "）");
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine("[S" + (i + 1) + "] " + (r.Title ?? "").Trim());
                sb.AppendLine("     " + (r.Url ?? ""));
                var snippet = (r.Snippet ?? "").Trim();
                if (snippet.Length > 0)
                    sb.AppendLine("     " + SearchService.Truncate(snippet, 300));
            }
            return sb.ToString().TrimEnd();
        }

        private static List<string> RulesHead(string query, string tail)
        {
            return new List<string>
            {
                "================ 联网检索结果（本地插件自动注入，非用户输入） ================",
                "检索时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                "检索词：" + query,
                tail,
            };
        }

        /// <summary>注入块：时间锚点 + 引用编号 + 明确允许忽略无关结果 + 规定答题顺序。</summary>
        public static string BuildContextBlock(string query, int nResults, string rendered,
            FetchedPage fetched, bool weak)
        {
            var head = "已采纳结果数：" + nResults;
            if (weak)
                head += "（**低置信度**：这些结果与问题的字面相关性很弱，很可能文不对题；能不用就不用，用了必须说明这是弱证据）";
            var parts = RulesHead(query, head);
            parts.AddRange(new[]
            {
                "",
                "【使用规则 —— 必须遵守】",
                "1. 下面是可能相关的外部网页摘录，可能过时、片面、甚至与问题无关。",
                "2. 只在与问题确实相关时使用；明显不相关就忽略，并说明「检索结果与问题无关」。",
                "3. 引用具体事实时标注来源编号（如 [S1]）。没有来源支撑的内容必须声明为你的推测。",
                "4. 检索结果与你的知识冲突时，明确指出冲突，并说明你采信哪个、为什么。",
                "5. 不要整段复述网页原文，只提取与问题相关的结论。",
                "6. 回答顺序：先给判断依据，再给结论；最后列出仍不确定的点。",
                "==============================================================================",
                "",
                "【检索结果】",
                rendered,
            });
            if (fetched != null && !string.IsNullOrEmpty(fetched.Text))
            {
                parts.Add("");
                parts.Add("---");
                parts.Add("【其中一条结果的网页正文（可能夹带无关内容，只取相关的部分）】");
                parts.Add("");
                parts.Add(fetched.Text);
            }
            return "\n\n---\n" + string.Join("\n", parts);
        }

        /// <summary>一条都没搜到时：让模型承认搜不到。这是整条链路里最重要的反幻觉路径。</summary>
        public static string BuildEmptyContextBlock(string query, string reason)
        {
            var parts = RulesHead(query, "结果：**本次检索没有找到与问题相关的内容**（" + reason + "）。");
            parts.AddRange(new[]
            {
                "",
                "【使用规则 —— 必须遵守】",
                "1. 明确告诉用户「这次联网没有搜到有用信息」，不要假装检索成功。",
                "2. 然后基于你自己的知识回答，并说明哪些部分可能已过时。",
                "3. **不要编造检索结果，也不要引用任何未提供的链接。**",
                "==============================================================================",
            });
            return "\n\n---\n" + string.Join("\n", parts);
        }

        /// <summary>把注入块追加到最后一条 user 消息末尾（防止系统提示词压过它）。</summary>
        public static void AppendToLastUserMessage(List<ChatMessage> messages, string block)
        {
            if (messages == null || string.IsNullOrEmpty(block)) return;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role != "user") continue;
                if (string.IsNullOrEmpty(messages[i].Content)) continue;
                messages[i].Content = messages[i].Content + block;
                return;
            }
        }
    }
}
