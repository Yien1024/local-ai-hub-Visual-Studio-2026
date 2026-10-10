using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LocalAIHub.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace LocalAIHub
{
    /// <summary>
    /// VS 包入口。
    ///
    /// 刻意做成"最轻"的形态（前车之鉴：有第三方扩展在 VS2026 里把 IDE 卡死）：
    ///   - 不注册 MEF 组件；不设 AllowsBackgroundLoad；不自动加载。
    ///   - 只有用户点菜单/工具窗口时才实例化。
    ///   - InitializeAsync 里只做注册，不做任何 I/O。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = false)]
#pragma warning disable CS0618 // InstalledProductRegistration 已过时（VS 不再读它），但写上无害且便于"帮助→关于"显示
    [InstalledProductRegistration("Local AI Hub", "本地大模型对话 + 联网搜索 + 模型管理", "1.0.0")]
#pragma warning restore CS0618
    // 注意：这里刻意没有 [ProvideMenuResource("Menus.ctmenu", 1)]。
    // 本机没有 VSCT 编译器（VS 安装里既无 VSSDK 也无 VSCTCompiler.exe），
    // 所以菜单/命令/工具窗口全部由 LocalAIHub.pkgdef 手工注册。
    // 如果声明了 ProvideMenuResource 却没有对应的 .ctmenu 资源，VS 加载包时会失败。
    [ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed,
        Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]   // 停靠在解决方案资源管理器旁
    [Guid(PackageGuidString)]
    [ProvideOptionPage(typeof(HubOptionsPage), "Local AI Hub", "常规", 0, 0, true)]
    public sealed class LocalAIHubPackage : Package
    {
        public const string PackageGuidString = "8f2a1c74-3b6d-4e51-9a0c-6d7e5b4a3120";
        public const string CommandSetGuidString = "6d5b2e91-8a41-4c73-b0e6-1f9a3c7d2b58";
        public const string ToolWindowGuidString = "3c9a7e15-52d8-4f61-ab34-7e0b9c1d8f26";

        private static HubServices _services;

        /// <summary>全局服务单例（工具窗口从这里拿 Ollama 客户端和设置）。</summary>
        public static HubServices Services
        {
            get
            {
                if (_services == null)
                {
                    try { _services = new HubServices(); }
                    catch (Exception ex) { Log.Error("Services 初始化", ex); throw; }
                }
                return _services;
            }
        }

        protected override void Initialize()
        {
            try
            {
                base.Initialize();
                Log.Info("LocalAIHubPackage.Initialize 开始 (VS " + GetVsVersion() + ")");

                var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
                if (mcs != null)
                {
                    AddCommand(mcs, 0x100, OpenChat);
                    AddCommand(mcs, 0x101, OpenSearchDialog);
                }
                else
                {
                    Log.Info("拿不到 IMenuCommandService，菜单命令未注册（工具窗口仍可从 视图→其他窗口 打开）");
                }
                Log.Info("LocalAIHubPackage.Initialize 完成");
            }
            catch (Exception ex)
            {
                // 绝不把异常抛回 VS —— 抛出去就是"装了个扩展，IDE 起不来"。
                Log.Error("LocalAIHubPackage.Initialize", ex);
            }
        }

        private static void AddCommand(OleMenuCommandService mcs, int cmdId, EventHandler handler)
        {
            try
            {
                var id = new CommandID(new Guid(CommandSetGuidString), cmdId);
                mcs.AddCommand(new MenuCommand(handler, id));
            }
            catch (Exception ex)
            {
                Log.Error("AddCommand 0x" + cmdId.ToString("x"), ex);
            }
        }

        private void OpenChat(object sender, EventArgs e)
        {
            try
            {
                var window = FindToolWindow(typeof(ChatToolWindow), 0, true) as ChatToolWindow;
                if (window == null || window.Frame == null)
                {
                    Log.Info("工具窗口创建失败（FindToolWindow 返回 null）");
                    return;
                }
                var frame = (IVsWindowFrame)window.Frame;
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                Log.Error("OpenChat", ex);
            }
        }

        private void OpenSearchDialog(object sender, EventArgs e)
        {
            try
            {
                // 复用聊天窗口：直接打开并预填"[search] "提示用户这里会强制联网
                OpenChat(sender, e);
            }
            catch (Exception ex)
            {
                Log.Error("OpenSearchDialog", ex);
            }
        }

        private static string GetVsVersion()
        {
            // 注意：尝试过用 IVsShell.GetProperty(VSSPROPID_ReleaseVersion) 拿版本，
            // 但 VS 2026 里 __VSSPROPID5 枚举不在这套引用程序集里（要扯进更多 interop 程序集，
            // 对一个只想显示版本号的日志来说不值得）。用 Shell 程序集版本更稳。
            try
            {
                var asm = typeof(Microsoft.VisualStudio.Shell.Package).Assembly;
                var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                return "shell " + fv.FileVersion + " / devenv " + fv.ProductVersion;
            }
            catch (Exception ex)
            {
                Log.Error("GetVsVersion", ex);
                return "?";
            }
        }
    }
}
