using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LocalAIHub.Core;
using Microsoft.VisualStudio.Shell;

namespace LocalAIHub
{
    /// <summary>
    /// 工具 → 选项 → Local AI Hub。
    /// 直接绑定 Core 的 HubSettings，改完即时生效并落盘到
    /// %LOCALAPPDATA%\LocalAIHub\settings.json（和聊天窗口共用同一份设置）。
    /// </summary>
    [Guid("a4d7c913-6b28-4e05-9f71-2c8d6a4b7e30")]
    public sealed class HubOptionsPage : DialogPage
    {
        private readonly HubSettings _settings;

        public HubOptionsPage()
        {
            try { _settings = SettingsStore.Load(); }
            catch (Exception ex) { Log.Error("HubOptionsPage 构造", ex); _settings = new HubSettings(); }
        }

        [Category("连接")]
        [DisplayName("Ollama 地址")]
        [Description("例如 http://127.0.0.1:11434")]
        public string OllamaEndpoint
        {
            get { return _settings.OllamaEndpoint; }
            set { _settings.OllamaEndpoint = value; }
        }

        [Category("连接")]
        [DisplayName("默认模型")]
        [Description("留空则用列表里最小的那个。例如 qwen3-coder:30b")]
        public string DefaultModel
        {
            get { return _settings.DefaultModel; }
            set { _settings.DefaultModel = value; }
        }

        [Category("联网搜索")]
        [DisplayName("启用联网搜索")]
        [Description("关闭后所有对话都不联网（也可以用消息里的 [no-search] 单次关闭）")]
        public bool WebSearchEnabled
        {
            get { return _settings.WebSearchEnabled; }
            set { _settings.WebSearchEnabled = value; }
        }

        [Category("联网搜索")]
        [DisplayName("搜索模式")]
        [Description("auto=按问题内容自动判定；always=每次都搜；never=从不搜")]
        [TypeConverter(typeof(SearchModeConverter))]
        public string SearchMode
        {
            get { return _settings.SearchMode; }
            set { _settings.SearchMode = value; }
        }

        [Category("联网搜索")]
        [DisplayName("最高注入条数")]
        [Description("一次最多把几条来源拼进 prompt。宁少勿滥：一条相关的胜过六条噪音。")]
        public int MaxInjected
        {
            get { return _settings.MaxInjected; }
            set { _settings.MaxInjected = Math.Max(0, Math.Min(10, value)); }
        }

        [Category("联网搜索")]
        [DisplayName("相关度阈值（普通问题）")]
        [Description("0~1。低于它的搜索结果不注入。默认 0.30")]
        public double RelevanceMin
        {
            get { return _settings.RelevanceMin; }
            set { _settings.RelevanceMin = Math.Max(0, Math.Min(1, value)); }
        }

        [Category("联网搜索")]
        [DisplayName("相关度阈值（时效问题）")]
        [Description("问最新版本/价格/新闻这类问题时放宽到它。默认 0.15")]
        public double RelevanceMinRealtime
        {
            get { return _settings.RelevanceMinRealtime; }
            set { _settings.RelevanceMinRealtime = Math.Max(0, Math.Min(1, value)); }
        }

        [Category("联网搜索")]
        [DisplayName("抓正文硬超时（秒）")]
        [Description("urlopen/HttpClient 的 timeout 不覆盖 DNS 解析，实测有页面挂 145 秒。默认 8")]
        public int FetchDeadlineSeconds
        {
            get { return _settings.FetchDeadlineSeconds; }
            set { _settings.FetchDeadlineSeconds = Math.Max(2, Math.Min(60, value)); }
        }

        [Category("生成参数")]
        [DisplayName("温度")]
        [Description("Qwen3-Coder 官方推荐 0.7")]
        public double Temperature
        {
            get { return _settings.Temperature; }
            set { _settings.Temperature = Math.Max(0, Math.Min(2, value)); }
        }

        [Category("生成参数")]
        [DisplayName("上下文长度 num_ctx")]
        [Description("要和 Ollama 的 OLLAMA_CONTEXT_LENGTH 对齐。默认 65536")]
        public int ContextLength
        {
            get { return _settings.ContextLength; }
            set { _settings.ContextLength = Math.Max(2048, value); }
        }

        [Category("生成参数")]
        [DisplayName("最大输出 token")]
        [Description("thinking 模型（如 qwen3:4b）会先用预算思考，给小了正文会是空的。默认 16384")]
        public int MaxOutputTokens
        {
            get { return _settings.MaxOutputTokens; }
            set { _settings.MaxOutputTokens = Math.Max(256, value); }
        }

        [Category("生成参数")]
        [DisplayName("模型常驻显存")]
        [Description("keep_alive=-1，避免每次第一发都要等十几秒重新加载")]
        public bool KeepAliveForever
        {
            get { return _settings.KeepAliveForever; }
            set { _settings.KeepAliveForever = value; }
        }

        [Category("Copilot BYOM")]
        [DisplayName("BYOM Base URL")]
        [Description("注册进 Copilot 时用的端点。直连 Ollama 用 http://127.0.0.1:11434/v1；走你自己的联网代理则填代理的 /v1 地址")]
        public string ByomBaseUrl
        {
            get { return _settings.ByomBaseUrl; }
            set { _settings.ByomBaseUrl = value; }
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try
            {
                base.OnApply(e);
                if (e.ApplyBehavior == ApplyKind.Apply)
                {
                    var ok = SettingsStore.Save(_settings);
                    Log.Info("选项页已应用，保存" + (ok ? "成功" : "失败") + " -> " + SettingsStore.FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error("HubOptionsPage.OnApply", ex);
            }
        }
    }

    /// <summary>让"搜索模式"在属性网格里是个下拉框，而不是让人手打字符串。</summary>
    public sealed class SearchModeConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(new[] { "auto", "always", "never" });
        }
    }
}
