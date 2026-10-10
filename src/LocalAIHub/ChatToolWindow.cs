using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace LocalAIHub
{
    /// <summary>
    /// 工具窗口宿主。ToolWindowPane 只做壳，真正的 UI 在 ChatControl 里。
    /// ⚠️ 任何异常都不能冒泡到 VS：构造函数里包了 try/catch，失败时显示一个
    /// 纯文本框把错误显示出来（而不是让 IDE 弹异常对话框或卡住）。
    /// </summary>
    [Guid(ChatToolWindow.ToolWindowGuidString)]
    public sealed class ChatToolWindow : ToolWindowPane
    {
        public const string ToolWindowGuidString = "3c9a7e15-52d8-4f61-ab34-7e0b9c1d8f26";

        public ChatToolWindow() : base(null)
        {
            try
            {
                Caption = "Local AI Hub（本地大模型 · 联网）";
                var control = new ChatControl();
                Content = control;
                LocalAIHub.Core.Log.Info("ChatToolWindow 构造完成，Content=" + (Content != null));
            }
            catch (Exception ex)
            {
                LocalAIHub.Core.Log.Error("ChatToolWindow 构造", ex);
                Content = new FallbackView("工具窗口初始化失败：" + ex.GetType().Name + "\n"
                                           + ex.Message + "\n\n完整堆栈见：\n"
                                           + LocalAIHub.Core.Log.FilePath);
            }
        }
    }
}
