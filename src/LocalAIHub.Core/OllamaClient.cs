using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAIHub.Core
{
    /// <summary>
    /// Ollama HTTP 客户端（只用 BCL，不用 Ollama 的 C# SDK，少一个依赖）。
    /// 覆盖插件需要的全部能力：列模型 / 看显存占用 / 拉取（带进度）/ 删除 /
    /// 预加载与卸载 / 流式对话（NDJSON）。
    /// </summary>
    public sealed class OllamaClient : IDisposable
    {
        private readonly HttpClient _http;
        private string _endpoint;

        public string Endpoint
        {
            get { return _endpoint; }
            set { _endpoint = (value ?? "").TrimEnd('/'); }
        }

        public OllamaClient(string endpoint)
        {
            Endpoint = endpoint ?? "http://127.0.0.1:11434";
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LocalAIHub-VSIX/1.0");
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>服务器版本；连不上返回 null（不抛异常，UI 要能优雅显示"没启动"）。</summary>
        public async Task<string> GetVersionAsync(int timeoutSeconds = 3, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                var text = await GetStringAsync("/api/version", timeoutSeconds, ct).ConfigureAwait(false);
                var obj = Json.ParseObject(text);
                return obj.GetString("version");
            }
            catch (Exception ex)
            {
                Log.Info("Ollama 未响应 (" + Endpoint + "): " + ex.Message);
                return null;
            }
        }

        public async Task<List<OllamaModel>> ListModelsAsync(int timeoutSeconds = 10,
            CancellationToken ct = default(CancellationToken))
        {
            var list = new List<OllamaModel>();
            var text = await GetStringAsync("/api/tags", timeoutSeconds, ct).ConfigureAwait(false);
            var obj = Json.ParseObject(text);
            var arr = obj.GetArray("models");
            if (arr == null) return list;
            foreach (var item in arr)
            {
                var m = OllamaModel.FromJson(item.AsObject());
                if (m != null) list.Add(m);
            }
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public async Task<List<RunningModel>> ListRunningAsync(int timeoutSeconds = 5,
            CancellationToken ct = default(CancellationToken))
        {
            var list = new List<RunningModel>();
            var text = await GetStringAsync("/api/ps", timeoutSeconds, ct).ConfigureAwait(false);
            var obj = Json.ParseObject(text);
            var arr = obj.GetArray("models");
            if (arr == null) return list;
            foreach (var item in arr)
            {
                var m = RunningModel.FromJson(item.AsObject());
                if (m != null) list.Add(m);
            }
            return list;
        }

        // ---------------------------------------------------------------- 管理

        /// <summary>拉取模型并报告进度。progress 回调：(状态文本, 已完成字节, 总字节)。</summary>
        public async Task<bool> PullAsync(string model, Action<string, long, long> progress,
            CancellationToken ct = default(CancellationToken))
        {
            var payload = Json.Serialize(new Dictionary<string, object> { { "model", model }, { "stream", true } });
            using (var req = NewRequest("/api/pull", payload))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                    Log.Info("pull 失败 HTTP " + (int)resp.StatusCode + ": " + body);
                    if (progress != null) progress("失败：HTTP " + (int)resp.StatusCode + " " + Truncate(body, 200), 0, 0);
                    return false;
                }
                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (ct.IsCancellationRequested) return false;
                        if (line.Trim().Length == 0) continue;
                        Dictionary<string, object> obj;
                        try { obj = Json.ParseObject(line); }
                        catch { continue; }
                        var status = obj.GetString("status") ?? "";
                        var completed = obj.GetLong("completed");
                        var total = obj.GetLong("total");
                        var err = obj.GetString("error");
                        if (progress != null) progress(err != null ? "错误：" + err : status, completed, total);
                        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)) return true;
                        if (err != null) return false;
                    }
                }
                return true;
            }
        }

        public async Task<bool> DeleteAsync(string model, CancellationToken ct = default(CancellationToken))
        {
            var payload = Json.Serialize(new Dictionary<string, object> { { "model", model } });
            using (var req = NewRequest("/api/delete", payload))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Info("delete 失败 HTTP " + (int)resp.StatusCode + ": "
                             + await SafeReadAsync(resp, ct).ConfigureAwait(false));
                    return false;
                }
                return true;
            }
        }

        /// <summary>预加载到显存（keep_alive=-1 常驻）或卸载（keep_alive=0）。</summary>
        public async Task<string> SetKeepAliveAsync(string model, bool keepForever,
            CancellationToken ct = default(CancellationToken))
        {
            var payload = Json.Serialize(new Dictionary<string, object>
            {
                { "model", model },
                { "prompt", "" },
                { "keep_alive", keepForever ? -1 : 0 },
                { "options", new Dictionary<string, object> { { "num_predict", 0 } } },
            });
            using (var req = NewRequest("/api/generate", payload))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Info("keep_alive 设置失败 HTTP " + (int)resp.StatusCode + ": " + Truncate(body, 300));
                    return "失败：HTTP " + (int)resp.StatusCode + " " + Truncate(body, 200);
                }
                return keepForever ? "已常驻显存（keep_alive=-1）" : "已请求卸载（keep_alive=0）";
            }
        }

        // ---------------------------------------------------------------- 对话

        /// <summary>
        /// 流式对话。onDelta 每来一段正文就回调一次；onReasoning 收思考通道；
        /// 返回本次统计（含 token 数、耗时）。取消通过 ct。
        /// </summary>
        public async Task<GenerationStats> ChatStreamAsync(
            string model,
            List<ChatMessage> messages,
            double temperature,
            int numCtx,
            int numPredict,
            bool keepForever,
            Action<string> onDelta,
            Action<string> onReasoning = null,
            CancellationToken ct = default(CancellationToken))
        {
            var stats = new GenerationStats { Model = model };
            var jsonMessages = new List<object>();
            foreach (var m in messages) jsonMessages.Add(m.ToJson());

            var options = new Dictionary<string, object>
            {
                { "temperature", temperature },
                { "num_ctx", numCtx },
                { "num_predict", numPredict },
            };
            var payload = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", jsonMessages },
                { "stream", true },
                { "options", options },
            };
            if (keepForever) payload["keep_alive"] = -1;

            var body = Json.Serialize(payload);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = true;
            long contentChars = 0;

            using (var req = NewRequest("/api/chat", body))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                    stats.Error = "HTTP " + (int)resp.StatusCode + " " + Truncate(err, 300);
                    stats.TotalSeconds = clock.Elapsed.TotalSeconds;
                    Log.Info("chat 失败: " + stats.Error);
                    return stats;
                }
                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (line.Trim().Length == 0) continue;
                        Dictionary<string, object> obj;
                        try { obj = Json.ParseObject(line); }
                        catch (Exception ex) { Log.Info("跳过非法 NDJSON 行: " + ex.Message); continue; }

                        var errField = obj.GetString("error");
                        if (errField != null)
                        {
                            stats.Error = errField;
                            break;
                        }
                        var message = obj.GetObject("message");
                        if (message != null)
                        {
                            var reasoning = message.GetString("reasoning");
                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                stats.ReasoningChars += reasoning.Length;
                                if (onReasoning != null) onReasoning(reasoning);
                            }
                            var content = message.GetString("content");
                            if (!string.IsNullOrEmpty(content))
                            {
                                if (first)
                                {
                                    stats.FirstTokenSeconds = clock.Elapsed.TotalSeconds;
                                    first = false;
                                }
                                contentChars += content.Length;
                                if (onDelta != null) onDelta(content);
                            }
                        }
                        if (obj.GetBool("done"))
                        {
                            stats.PromptTokens = obj.GetLong("prompt_eval_count");
                            stats.CompletionTokens = obj.GetLong("eval_count");
                            stats.DoneReason = obj.GetString("done_reason");
                            break;
                        }
                    }
                }
            }
            stats.TotalSeconds = clock.Elapsed.TotalSeconds;

            // thinking 模型的空回答陷阱（实测）：思考走单独的 reasoning 字段，
            // 预算被思考吃光时 content 是空的，UI 上表现为"一片空白"。
            // 这里显式标出来，让上层能自动加大预算重试，而不是让用户看白屏。
            if (string.IsNullOrEmpty(stats.Error) && contentChars == 0 && stats.ReasoningChars > 0)
            {
                stats.Error = "thinking 模型把输出预算吃光了（思考 " + stats.ReasoningChars
                              + " 字，正文 0 字，用掉 " + stats.CompletionTokens + "/" + numPredict
                              + " token）。已自动标记为可重试。";
                Log.Info("thinking 吃光预算: model=" + model + " reasoning=" + stats.ReasoningChars
                         + " eval=" + stats.CompletionTokens + " num_predict=" + numPredict);
            }
            return stats;
        }

        // ---------------------------------------------------------------- 内部

        private HttpRequestMessage NewRequest(string path, string jsonBody)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint + path);
            req.Content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json");
            return req;
        }

        private async Task<string> GetStringAsync(string path, int timeoutSeconds, CancellationToken outer)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(outer))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using (var resp = await _http.GetAsync(Endpoint + path, cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try { return await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return ""; }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        public void Dispose()
        {
            try { _http.Dispose(); } catch { }
        }
    }
}
