using System;
using System.Collections.Generic;

namespace LocalAIHub.Core
{
    /// <summary>搜索引擎返回的一条结果。</summary>
    public sealed class SearchResult
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Snippet { get; set; }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "title", Title ?? "" },
                { "url", Url ?? "" },
                { "snippet", Snippet ?? "" },
            };
        }
    }

    /// <summary>一次搜索的完整结果（含用了哪个引擎、原始条数、相关度）。</summary>
    public sealed class SearchResponse
    {
        public string Query { get; set; }
        public string Engine { get; set; }
        public int RawCount { get; set; }
        public List<SearchResult> Results { get; set; } = new List<SearchResult>();
        /// <summary>相关度最高的那条的分数，用于判断是否低置信度。</summary>
        public double TopScore { get; set; }
        public bool Weak { get; set; }
        public string Error { get; set; }
    }

    /// <summary>抓取到的网页正文。</summary>
    public sealed class FetchedPage
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Ollama /api/tags 里的一个模型。</summary>
    public sealed class OllamaModel
    {
        public string Name { get; set; }
        public string Model { get; set; }
        public long Size { get; set; }
        public string Digest { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
        public string Family { get; set; }
        public string ParameterSize { get; set; }
        public string QuantizationLevel { get; set; }

        public string DisplayName { get { return string.IsNullOrEmpty(Name) ? Model : Name; } }
        public string SizeText { get { return Size <= 0 ? "?" : (Size / 1073741824.0).ToString("0.00") + " GB"; } }
        public string DetailText
        {
            get
            {
                var bits = new List<string>();
                if (!string.IsNullOrEmpty(ParameterSize)) bits.Add(ParameterSize);
                if (!string.IsNullOrEmpty(QuantizationLevel)) bits.Add(QuantizationLevel);
                if (!string.IsNullOrEmpty(Family)) bits.Add(Family);
                return string.Join(" · ", bits);
            }
        }

        public static OllamaModel FromJson(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            var details = obj.GetObject("details");
            return new OllamaModel
            {
                Name = obj.GetString("name") ?? obj.GetString("model"),
                Model = obj.GetString("model"),
                Size = obj.GetLong("size"),
                Digest = obj.GetString("digest"),
                ModifiedAt = ParseTime(obj.GetString("modified_at")),
                Family = details.GetString("family"),
                ParameterSize = details.GetString("parameter_size"),
                QuantizationLevel = details.GetString("quantization_level"),
            };
        }

        internal static DateTimeOffset ParseTime(string s)
        {
            DateTimeOffset dto;
            if (!string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, out dto)) return dto;
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>/api/ps 里正在显存里的模型。</summary>
    public sealed class RunningModel
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public long SizeVram { get; set; }
        public int ContextLength { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>是否 100% 在显存里（不是就是 CPU/GPU 混合，会慢很多）。</summary>
        public bool FullyOnGpu { get { return SizeVram > 0 && SizeVram >= Size * 0.98; } }

        public string PlacementText
        {
            get
            {
                if (Size <= 0) return "?";
                return FullyOnGpu ? "100% GPU" : (SizeVram * 100 / Size) + "% GPU（混合，慢）";
            }
        }

        public string SizeText { get { return (Size / 1073741824.0).ToString("0.00") + " GB"; } }

        public string Summary
        {
            get
            {
                return Name + "  " + SizeText + "  " + PlacementText
                       + "  上下文 " + ContextLength
                       + (ExpiresAt == DateTimeOffset.MinValue || ExpiresAt.Year < 2000
                           ? "  常驻"
                           : "  到期 " + ExpiresAt.ToLocalTime().ToString("HH:mm:ss"));
            }
        }

        public static RunningModel FromJson(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            return new RunningModel
            {
                Name = obj.GetString("name") ?? obj.GetString("model"),
                Size = obj.GetLong("size"),
                SizeVram = obj.GetLong("size_vram"),
                ContextLength = (int)obj.GetLong("context_length"),
                ExpiresAt = OllamaModel.ParseTime(obj.GetString("expires_at")),
            };
        }
    }

    /// <summary>一次生成的统计（UI 底部状态栏用）。</summary>
    public sealed class GenerationStats
    {
        public string Model { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public double TotalSeconds { get; set; }
        public double FirstTokenSeconds { get; set; }
        public bool Searched { get; set; }
        public string SearchQuery { get; set; }
        public string SearchEngine { get; set; }
        public int SearchRaw { get; set; }
        public int SearchKept { get; set; }
        public bool SearchWeak { get; set; }
        public double SearchSeconds { get; set; }
        public double FetchSeconds { get; set; }
        public bool Fetched { get; set; }
        public string Error { get; set; }
        /// <summary>Ollama 返回的 done_reason（stop / length / ...）。</summary>
        public string DoneReason { get; set; }
        /// <summary>思考通道产出的字符数（thinking 模型走单独的 reasoning 字段，不占 content）。</summary>
        public long ReasoningChars { get; set; }

        /// <summary>
        /// 是否"思考把输出预算吃光了"：有思考、没有正文。
        /// 实测过：qwen3:4b-local 在 max_tokens=400 时 content 是空的、reasoning 有 620 字；
        /// 给到 2500 才吐出答案。UI 见到这个标志就翻倍预算自动重试一次。
        /// </summary>
        public bool ThinkingAteBudget
        {
            get
            {
                return !string.IsNullOrEmpty(Error)
                       && Error.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public double TokensPerSecond
        {
            get { return TotalSeconds <= 0 ? 0 : CompletionTokens / TotalSeconds; }
        }

        public string OneLine()
        {
            var s = Model + " · 输出 " + CompletionTokens + " tok / " + TotalSeconds.ToString("0.00")
                    + "s = " + TokensPerSecond.ToString("0.0") + " tok/s · 首字 "
                    + FirstTokenSeconds.ToString("0.00") + "s · prompt " + PromptTokens + " tok";
            s += Searched
                ? " · 联网 " + SearchEngine + " raw=" + SearchRaw + " kept=" + SearchKept
                  + (SearchWeak ? " (弱)" : "") + " 搜索 " + SearchSeconds.ToString("0.00") + "s 抓正文 "
                  + (Fetched ? FetchSeconds.ToString("0.00") + "s" : "跳过")
                : " · 未联网";
            if (!string.IsNullOrEmpty(Error)) s += " · 错误: " + Error;
            return s;
        }
    }

    /// <summary>插件设置（持久化到 %LOCALAPPDATA%\LocalAIHub\settings.json）。</summary>
    public sealed class HubSettings
    {
        public string OllamaEndpoint { get; set; }
        public string DefaultModel { get; set; }
        public bool WebSearchEnabled { get; set; }
        /// <summary>auto = 按问题自动判定；always = 每次都搜；never = 从不搜。</summary>
        public string SearchMode { get; set; }
        public double Temperature { get; set; }
        public int ContextLength { get; set; }
        public int MaxOutputTokens { get; set; }
        public bool KeepAliveForever { get; set; }
        public int FetchDeadlineSeconds { get; set; }
        public double RelevanceMin { get; set; }
        public double RelevanceMinRealtime { get; set; }
        public int MaxInjected { get; set; }
        public int MaxAutoSearches { get; set; }
        /// <summary>给 BYOM 注册用的 CustomURL（指向本机 Ollama 或代理）。</summary>
        public string ByomBaseUrl { get; set; }

        public HubSettings()
        {
            OllamaEndpoint = "http://127.0.0.1:11434";
            DefaultModel = "";
            WebSearchEnabled = true;
            SearchMode = "auto";
            Temperature = 0.7;
            ContextLength = 65536;
            MaxOutputTokens = 16384;
            KeepAliveForever = true;
            FetchDeadlineSeconds = 8;
            RelevanceMin = 0.30;
            RelevanceMinRealtime = 0.15;
            MaxInjected = 4;
            MaxAutoSearches = 2;
            ByomBaseUrl = "http://127.0.0.1:11434/v1";
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "ollamaEndpoint", OllamaEndpoint },
                { "defaultModel", DefaultModel ?? "" },
                { "webSearchEnabled", WebSearchEnabled },
                { "searchMode", SearchMode ?? "auto" },
                { "temperature", Temperature },
                { "contextLength", ContextLength },
                { "maxOutputTokens", MaxOutputTokens },
                { "keepAliveForever", KeepAliveForever },
                { "fetchDeadlineSeconds", FetchDeadlineSeconds },
                { "relevanceMin", RelevanceMin },
                { "relevanceMinRealtime", RelevanceMinRealtime },
                { "maxInjected", MaxInjected },
                { "maxAutoSearches", MaxAutoSearches },
                { "byomBaseUrl", ByomBaseUrl ?? "" },
            };
        }

        public static HubSettings FromJson(Dictionary<string, object> obj)
        {
            var s = new HubSettings();
            if (obj == null) return s;
            if (obj.Get("ollamaEndpoint") != null) s.OllamaEndpoint = obj.GetString("ollamaEndpoint");
            if (obj.Get("defaultModel") != null) s.DefaultModel = obj.GetString("defaultModel");
            if (obj.Get("webSearchEnabled") != null) s.WebSearchEnabled = obj.GetBool("webSearchEnabled", true);
            if (obj.Get("searchMode") != null) s.SearchMode = obj.GetString("searchMode");
            if (obj.Get("temperature") != null) s.Temperature = obj.GetDouble("temperature", 0.7);
            if (obj.Get("contextLength") != null) s.ContextLength = (int)obj.GetLong("contextLength", 65536);
            if (obj.Get("maxOutputTokens") != null) s.MaxOutputTokens = (int)obj.GetLong("maxOutputTokens", 8192);
            if (obj.Get("keepAliveForever") != null) s.KeepAliveForever = obj.GetBool("keepAliveForever", true);
            if (obj.Get("fetchDeadlineSeconds") != null) s.FetchDeadlineSeconds = (int)obj.GetLong("fetchDeadlineSeconds", 8);
            if (obj.Get("relevanceMin") != null) s.RelevanceMin = obj.GetDouble("relevanceMin", 0.30);
            if (obj.Get("relevanceMinRealtime") != null) s.RelevanceMinRealtime = obj.GetDouble("relevanceMinRealtime", 0.15);
            if (obj.Get("maxInjected") != null) s.MaxInjected = (int)obj.GetLong("maxInjected", 4);
            if (obj.Get("maxAutoSearches") != null) s.MaxAutoSearches = (int)obj.GetLong("maxAutoSearches", 2);
            if (obj.Get("byomBaseUrl") != null) s.ByomBaseUrl = obj.GetString("byomBaseUrl");
            return s;
        }
    }

    /// <summary>聊天里的一条消息。</summary>
    public sealed class ChatMessage
    {
        public string Role { get; set; }        // user / assistant / system
        public string Content { get; set; }
        /// <summary>是否联网搜索过（仅用于 UI 标记）。</summary>
        public bool Searched { get; set; }

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { { "role", Role }, { "content", Content ?? "" } };
        }
    }
}
