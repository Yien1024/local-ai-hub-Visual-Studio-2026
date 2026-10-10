using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalAIHub.Core;

namespace LocalAIHub
{
    /// <summary>
    /// 给 UI 用的服务容器。持有 Ollama 客户端、搜索链路、设置。
    /// 所有方法都是 async 的，UI 线程永不阻塞。
    /// </summary>
    public sealed class HubServices : IDisposable
    {
        private readonly ChatPipeline _pipeline;

        public HubSettings Settings { get; private set; }
        public event Action<string> LogLine;

        public HubServices()
        {
            Settings = SettingsStore.Load();
            _pipeline = new ChatPipeline(Settings);
            Log.Sink += line =>
            {
                var h = LogLine;
                if (h != null) { try { h(line); } catch { } }
            };
            Log.Info("HubServices 就绪：Ollama=" + Settings.OllamaEndpoint
                     + " 联网=" + Settings.WebSearchEnabled + " 模式=" + Settings.SearchMode);
        }

        public ChatPipeline Pipeline { get { return _pipeline; } }

        public void ReloadSettings()
        {
            Settings = SettingsStore.Load();
            _pipeline.ApplySettings(Settings);
            Log.Info("设置已重新加载");
        }

        public bool SaveSettings()
        {
            _pipeline.ApplySettings(Settings);
            return SettingsStore.Save(Settings);
        }

        // ------------------------------------------------------------ 模型

        public Task<string> GetVersionAsync(CancellationToken ct)
        {
            return _pipeline.Ollama.GetVersionAsync(3, ct);
        }

        public Task<List<OllamaModel>> ListModelsAsync(CancellationToken ct)
        {
            return _pipeline.Ollama.ListModelsAsync(10, ct);
        }

        public Task<List<RunningModel>> ListRunningAsync(CancellationToken ct)
        {
            return _pipeline.Ollama.ListRunningAsync(5, ct);
        }

        public Task<string> SetKeepAliveAsync(string model, bool keepForever, CancellationToken ct)
        {
            return _pipeline.Ollama.SetKeepAliveAsync(model, keepForever, ct);
        }

        public Task<bool> DeleteModelAsync(string model, CancellationToken ct)
        {
            return _pipeline.Ollama.DeleteAsync(model, ct);
        }

        public Task<bool> PullModelAsync(string model, Action<string, long, long> progress, CancellationToken ct)
        {
            return _pipeline.Ollama.PullAsync(model, progress, ct);
        }

        // ------------------------------------------------------------ 联网

        /// <summary>只做一次搜索并把结果给 UI 看（不进模型）。</summary>
        public async Task<string> SearchOnceAsync(string query, CancellationToken ct)
        {
            using (var http = new HttpFetcher())
            {
                var svc = new SearchService(http);
                var q = QueryDecision.ExtractSearchQuery(query);
                var resp = await svc.SearchAsync(q, 8, "auto", ct).ConfigureAwait(false);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("检索词：" + q);
                sb.AppendLine("引擎：" + resp.Engine + "  原始条数：" + resp.RawCount
                              + (resp.Error != null ? "  错误：" + resp.Error : ""));
                List<SearchResult> kept;
                double top;
                bool weak;
                var minScore = QueryDecision.IsRealtime(query)
                    ? Settings.RelevanceMinRealtime
                    : Settings.RelevanceMin;
                ContextBlocks.RankAndGate(resp.Results, q, minScore, Settings.MaxInjected, true,
                    out kept, out top, out weak);
                sb.AppendLine("过闸门后：" + kept.Count + " 条（最高分 " + top.ToString("0.00")
                              + "，阈值 " + minScore.ToString("0.00") + (weak ? "，低置信度" : "") + "）");
                sb.AppendLine();
                foreach (var r in kept)
                {
                    sb.AppendLine("· " + r.Title);
                    sb.AppendLine("  " + r.Url);
                    if (!string.IsNullOrEmpty(r.Snippet)) sb.AppendLine("  " + r.Snippet);
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ 对话

        /// <summary>
        /// 走完整链路：判定是否联网 → 搜索 → 注入 → 流式回答。
        /// 返回 false 表示被取消或失败（细节看 onStatus 的回调）。
        /// </summary>
        public async Task<GenerationStats> AskAsync(
            string model,
            List<ChatMessage> history,
            string userText,
            bool forceSearch,
            Action<string> onDelta,
            Action<string> onReasoning,
            Action<string> onStatus,
            CancellationToken ct)
        {
            var stats = new GenerationStats { Model = model };
            try
            {
                var outgoing = new List<ChatMessage>(history);
                var text = userText;
                if (forceSearch && !text.Contains("[search]")) text = text + " [search]";
                outgoing.Add(new ChatMessage("user", text));

                // 1) 联网 + 注入
                if (Settings.WebSearchEnabled)
                {
                    onStatus("正在判断是否需要联网…");
                    InjectionResult inj;
                    try
                    {
                        inj = await _pipeline.BuildInjectionAsync(text, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Error("联网阶段", ex);
                        inj = null;
                        onStatus("联网失败，改为不联网回答：" + ex.Message);
                    }
                    if (inj != null)
                    {
                        if (!string.IsNullOrEmpty(inj.Block) && !inj.FromCache)
                        {
                            ContextBlocks.AppendToLastUserMessage(outgoing, inj.Block);
                            stats.Searched = true;
                            stats.SearchQuery = inj.Query;
                            stats.SearchEngine = inj.Engine;
                            stats.SearchRaw = inj.Raw;
                            stats.SearchKept = inj.Kept;
                            stats.SearchWeak = inj.Weak;
                            stats.SearchSeconds = inj.SearchSeconds;
                            stats.FetchSeconds = inj.FetchSeconds;
                            stats.Fetched = inj.Fetched;
                        }
                        else if (inj.FromCache)
                        {
                            ContextBlocks.AppendToLastUserMessage(outgoing, inj.Block);
                            stats.Searched = true;
                            stats.SearchQuery = inj.Query;
                            stats.SearchEngine = inj.Engine;
                        }
                        onStatus("联网：" + inj.Reason);
                    }
                }

                // 2) 流式回答
                onStatus("正在生成…");
                stats = await _pipeline.ChatAsync(model, outgoing, onDelta, onReasoning, ct)
                    .ConfigureAwait(false);
                stats.Searched = stats.Searched || outgoing.Count > history.Count + 1;

                // 3) thinking 模型吃光预算 → 自动加倍重试一次
                if (stats.ThinkingAteBudget && stats.CompletionTokens < Settings.MaxOutputTokens)
                {
                    var bigger = Math.Min(Settings.MaxOutputTokens * 2, 32768);
                    Log.Info("检测到 thinking 吃光预算，自动加倍重试 num_predict=" + bigger);
                    onStatus("思考占满了输出预算，已自动加大到 " + bigger + " token 重试…");
                    onDelta("\n\n（思考把输出预算占满了，正在用更大的预算重试…）\n\n");
                    var retry = await _pipeline.Ollama.ChatStreamAsync(model, outgoing,
                        Settings.Temperature, Settings.ContextLength, bigger,
                        Settings.KeepAliveForever, onDelta, onReasoning, ct).ConfigureAwait(false);
                    retry.Searched = stats.Searched;
                    retry.SearchQuery = stats.SearchQuery;
                    retry.SearchEngine = stats.SearchEngine;
                    retry.SearchRaw = stats.SearchRaw;
                    retry.SearchKept = stats.SearchKept;
                    retry.SearchWeak = stats.SearchWeak;
                    retry.SearchSeconds = stats.SearchSeconds;
                    retry.Fetched = stats.Fetched;
                    retry.FetchSeconds = stats.FetchSeconds;
                    return retry;
                }
                return stats;
            }
            catch (OperationCanceledException)
            {
                stats.Error = "已取消";
                return stats;
            }
            catch (Exception ex)
            {
                Log.Error("AskAsync", ex);
                stats.Error = ex.GetType().Name + ": " + ex.Message;
                return stats;
            }
        }

        public void Dispose()
        {
            try { _pipeline.Dispose(); } catch { }
        }
    }
}
