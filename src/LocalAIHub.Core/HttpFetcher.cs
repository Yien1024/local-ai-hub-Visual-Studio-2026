using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 极小的日志器：写文件 + 可选回调（给 UI 日志面板用）。
    /// 绝不抛异常——日志失败不能影响主流程。
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        public static event Action<string> Sink;

        public static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    var dir = Path.Combine(Path.GetTempPath(), "LocalAIHub");
                    try { Directory.CreateDirectory(dir); } catch { }
                    _path = Path.Combine(dir, "localaihub.log");
                }
                return _path;
            }
        }

        public static void Info(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
            try { Sink?.Invoke(line); } catch { }
        }

        public static void Error(string where, Exception ex)
        {
            Info("ERROR " + where + ": " + ex.GetType().Name + ": " + ex.Message
                 + Environment.NewLine + ex.StackTrace);
        }
    }

    /// <summary>
    /// 带硬墙钟上限的 HTTP。要点（来自实测，见 docs/SHARED-FACTS.md）：
    /// HttpClient 的 Timeout 不覆盖 DNS 解析，实测有 URL 挂 145 秒；
    /// 所以这里用 CancellationToken 做外层上限，超时后主动放弃（不阻塞调用方）。
    /// 另外自动解 gzip/deflate（Bing 会压缩），并做 charset 嗅探（百度返回 gb18030）。
    /// </summary>
    public sealed class HttpFetcher : IDisposable
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private readonly HttpClient _client;

        public HttpFetcher()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            };
            _client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,   // 由 CancellationToken 管
            };
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language",
                "zh-CN,zh;q=0.9,en;q=0.8");
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        }

        /// <summary>GET 一个 URL，返回文本；超时/失败抛 OperationCanceledException 或 HttpRequestException。</summary>
        public async Task<string> GetStringAsync(string url, int timeoutSeconds, long maxBytes = 4_000_000,
            CancellationToken outer = default(CancellationToken))
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(outer))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using (var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var buffer = new byte[81920];
                        using (var ms = new MemoryStream())
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)
                                       .ConfigureAwait(false)) > 0)
                            {
                                ms.Write(buffer, 0, read);
                                if (ms.Length >= maxBytes) break;
                            }
                            var bytes = ms.ToArray();
                            string charset = null;
                            try
                            {
                                var head = Encoding.GetEncoding("latin1")
                                    .GetString(bytes, 0, Math.Min(4096, bytes.Length));
                                var m = System.Text.RegularExpressions.Regex.Match(head, "charset=[\"']?([\\w-]+)",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success) charset = m.Groups[1].Value;
                            }
                            catch { }
                            return Decode(bytes, charset);
                        }
                    }
                }
            }
        }

        private static string Decode(byte[] bytes, string declaredCharset)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(declaredCharset)) candidates.Add(declaredCharset);
            candidates.Add("utf-8");
            candidates.Add("gb18030");
            foreach (var name in candidates)
            {
                try
                {
                    var enc = Encoding.GetEncoding(name,
                        new EncoderReplacementFallback("?"), new DecoderExceptionFallback());
                    return enc.GetString(bytes);
                }
                catch (DecoderFallbackException) { }
                catch (ArgumentException) { }
            }
            try { return Encoding.GetEncoding("gb18030").GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }

        public void Dispose()
        {
            try { _client.Dispose(); } catch { }
        }
    }

    /// <summary>把"可能挂住"的任务包一层墙钟上限。超时返回默认值，不抛。</summary>
    public static class Deadline
    {
        public static async Task<T> RunAsync<T>(Task<T> task, int seconds, T fallback)
        {
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)))
                .ConfigureAwait(false);
            if (completed != task)
            {
                Log.Info("deadline exceeded (" + seconds + "s), abandoning task");
                return fallback;
            }
            try { return await task.ConfigureAwait(false); }
            catch (Exception ex) { Log.Info("task failed: " + ex.Message); return fallback; }
        }
    }
}
