using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAIHub.Core;

namespace LocalAIHub
{
    /// <summary>
    /// 聊天面板。
    ///
    /// 硬规则（这是"不把 IDE 搞卡"的关键）：
    ///   1. 任何 HTTP 都在线程池上 await，UI 线程只做画界面。
    ///   2. 每个事件处理器都包 try/catch，异常进日志 + 状态栏，不弹给 VS。
    ///   3. 关闭/取消时能干净地停掉在飞的请求。
    /// </summary>
    public sealed class ChatControl : UserControl
    {
        private readonly HubServices _services;
        private readonly Dispatcher _ui;

        private ComboBox _modelBox;
        private Button _refreshBtn;
        private Button _manageBtn;
        private CheckBox _searchBox;
        private ComboBox _searchModeBox;
        private TextBox _output;
        private TextBox _input;
        private Button _sendBtn;
        private Button _stopBtn;
        private Button _clearBtn;
        private TextBlock _status;
        private TextBlock _connText;
        private Expander _logExpander;
        private TextBox _logBox;

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private CancellationTokenSource _cts;
        private bool _busy;
        private int _logLines;

        public ChatControl()
        {
            _ui = Dispatcher.CurrentDispatcher;
            try
            {
                _services = LocalAIHubPackage.Services;
                BuildUi();
                _services.LogLine += OnLogLine;
                Loaded += (s, e) => SafeFire(RefreshModelsAsync());
                Unloaded += (s, e) => { try { _services.LogLine -= OnLogLine; } catch { } };
                Log.Info("ChatControl 已构建");
            }
            catch (Exception ex)
            {
                Log.Error("ChatControl 构造", ex);
                Content = BuildErrorPanel(ex);
            }
        }

        // ------------------------------------------------------------ 界面

        private void BuildUi()
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(0) };

            // ---------- 顶部工具条 ----------
            var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 2) };
            top.Children.Add(Label("模型:"));
            _modelBox = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 6, 4), ToolTip = "本机 Ollama 里已有/正在运行的模型" };
            _modelBox.SelectionChanged += (s, e) => OnModelChanged();
            top.Children.Add(_modelBox);

            _refreshBtn = Btn("刷新", async () => await RefreshModelsAsync(), "重新读取 ollama 的模型列表");
            _manageBtn = Btn("模型管理…", (s, e) => OpenModelManager(), "拉取 / 删除 / 常驻显存");
            top.Children.Add(_refreshBtn);
            top.Children.Add(_manageBtn);

            _searchBox = new CheckBox
            {
                Content = "联网搜索",
                IsChecked = _services.Settings.WebSearchEnabled,
                Margin = new Thickness(10, 6, 4, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "开启后按下面的模式决定是否给模型注入实时检索结果",
            };
            _searchBox.Checked += (s, e) => { _services.Settings.WebSearchEnabled = true; PersistSettings(); };
            _searchBox.Unchecked += (s, e) => { _services.Settings.WebSearchEnabled = false; PersistSettings(); };
            top.Children.Add(_searchBox);

            _searchModeBox = new ComboBox { Width = 118, Margin = new Thickness(0, 0, 4, 4), ToolTip = "auto=按问题判定；always=每次都搜；never=从不搜" };
            _searchModeBox.Items.Add("auto 自动");
            _searchModeBox.Items.Add("always 总是");
            _searchModeBox.Items.Add("never 从不");
            _searchModeBox.SelectedIndex = _services.Settings.SearchMode == "always" ? 1
                : _services.Settings.SearchMode == "never" ? 2 : 0;
            _searchModeBox.SelectionChanged += (s, e) =>
            {
                var mode = new[] { "auto", "always", "never" }[Math.Max(0, _searchModeBox.SelectedIndex)];
                _services.Settings.SearchMode = mode;
                PersistSettings();
            };
            top.Children.Add(_searchModeBox);

            top.Children.Add(Btn("BYOM…", (s, e) => OpenByomDialog(), "把本地模型注册进 VS Copilot 的自带模型(BYOM)"));
            top.Children.Add(Btn("搜索测试…", (s, e) => OpenSearchTest(), "不经过模型，直接看搜索引擎返回什么"));
            _clearBtn = Btn("清空", (s, e) => ClearConversation(), "清空当前会话（同时清掉检索缓存）");
            top.Children.Add(_clearBtn);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // ---------- 状态栏 ----------
            var statusBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 4) };
            _connText = new TextBlock { Text = "连接中…", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 12, 0) };
            _status = new TextBlock { Text = "就绪", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Gray };
            statusBar.Children.Add(_connText);
            statusBar.Children.Add(_status);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // ---------- 底部输入区 ----------
            var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 0, 6, 6) };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _sendBtn = new Button { Content = "发送 (Ctrl+Enter)", MinWidth = 130, Padding = new Thickness(8, 4, 8, 4), IsDefault = false };
            _sendBtn.Click += async (s, e) => await SafeSendAsync();
            _stopBtn = new Button { Content = "停止", MinWidth = 60, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false, Padding = new Thickness(8, 4, 8, 4) };
            _stopBtn.Click += (s, e) => CancelCurrent();
            btnRow.Children.Add(_sendBtn);
            btnRow.Children.Add(_stopBtn);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            bottom.Children.Add(btnRow);

            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 62,
                MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ToolTip = "Ctrl+Enter 发送；Shift+Enter 换行。在消息里写 [search] 强制联网、[no-search] 强制不联网。",
            };
            _input.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    e.Handled = true;
                    SafeFire(SafeSendAsync());
                }
            };
            bottom.Children.Add(_input);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            // ---------- 中部：输出 + 日志 ----------
            var center = new DockPanel { LastChildFill = true };

            _logExpander = new Expander { Header = "运行日志（排查用）", IsExpanded = false, Margin = new Thickness(6, 0, 6, 0) };
            _logBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                Height = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
            };
            _logExpander.Content = _logBox;
            DockPanel.SetDock(_logExpander, Dock.Bottom);
            center.Children.Add(_logExpander);

            _output = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(6, 4, 6, 4),
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
            };
            _output.SetValue(ScrollViewer.CanContentScrollProperty, true);
            center.Children.Add(_output);

            root.Children.Add(center);
            Content = root;

            Append("Local AI Hub 就绪。\n"
                 + "· 本机模型直接对话；勾选「联网搜索」后会自动判定是否注入实时检索结果。\n"
                 + "· 消息里写 [search] / [no-search] 可以手动强制。\n"
                 + "· Ctrl+Enter 发送。\n\n");
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Margin = new Thickness(0, 6, 4, 4), VerticalAlignment = VerticalAlignment.Center };
        }

        private static Button Btn(string text, RoutedEventHandler handler, string tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            b.Click += (s, e) => { try { handler(s, e); } catch (Exception ex) { Log.Error("按钮 " + text, ex); MessageBox.Show(ex.Message, text); } };
            return b;
        }

        private static Button Btn(string text, Func<Task> handler, string tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            b.Click += async (s, e) => { try { await handler(); } catch (Exception ex) { Log.Error("按钮 " + text, ex); MessageBox.Show(ex.Message, text); } };
            return b;
        }

        private static UIElement BuildErrorPanel(Exception ex)
        {
            return new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Text = "Local AI Hub 界面初始化失败：\n\n" + ex
                       + "\n\n完整日志：" + Log.FilePath,
                Margin = new Thickness(8),
            };
        }

        // ------------------------------------------------------------ 模型

        private async Task RefreshModelsAsync()
        {
            try
            {
                _status.Text = "正在读取模型列表…";
                var version = await _services.GetVersionAsync(CancellationToken.None).ConfigureAwait(true);
                if (version == null)
                {
                    _connText.Text = "● Ollama 未响应（" + _services.Settings.OllamaEndpoint + "）";
                    _connText.Foreground = Brushes.Firebrick;
                    _status.Text = "请确认 Ollama 正在运行；模型管理里可以改地址。";
                    return;
                }
                _connText.Text = "● Ollama " + version + " 已连接";
                _connText.Foreground = Brushes.SeaGreen;

                var models = await _services.ListModelsAsync(CancellationToken.None).ConfigureAwait(true);
                var running = await _services.ListRunningAsync(CancellationToken.None).ConfigureAwait(true);
                var runningNames = new HashSet<string>(running.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

                var previous = _modelBox.SelectedItem as OllamaModel;
                var previousName = previous != null ? previous.DisplayName : _services.Settings.DefaultModel;

                _modelBox.Items.Clear();
                foreach (var m in models)
                {
                    m.Name = m.DisplayName + (runningNames.Contains(m.DisplayName) ? "   [常驻显存]" : "");
                    _modelBox.Items.Add(m);
                }
                if (_modelBox.Items.Count == 0)
                {
                    _status.Text = "Ollama 里还没有任何模型。用「模型管理…」拉一个（例如 qwen3-coder:30b）。";
                    return;
                }

                var pick = _modelBox.Items.Cast<OllamaModel>()
                    .FirstOrDefault(x => x.DisplayName == previousName)
                    ?? _modelBox.Items.Cast<OllamaModel>().OrderBy(x => x.Size).First();
                _modelBox.SelectedItem = pick;

                var runningText = running.Count == 0
                    ? "当前没有模型常驻显存"
                    : string.Join(" | ", running.Select(r => r.Summary));
                _status.Text = models.Count + " 个模型 · " + runningText;
                Log.Info("模型列表已刷新：" + models.Count + " 个，运行中 " + running.Count + " 个");
            }
            catch (Exception ex)
            {
                Log.Error("RefreshModelsAsync", ex);
                _status.Text = "读取模型失败：" + ex.Message;
            }
        }

        private void OnModelChanged()
        {
            try
            {
                var m = _modelBox.SelectedItem as OllamaModel;
                if (m == null) return;
                _services.Settings.DefaultModel = m.DisplayName;
                PersistSettings();
            }
            catch (Exception ex) { Log.Error("OnModelChanged", ex); }
        }

        private string CurrentModel()
        {
            var m = _modelBox.SelectedItem as OllamaModel;
            return m == null ? null : m.DisplayName;
        }

        // ------------------------------------------------------------ 发送

        private async Task SafeSendAsync()
        {
            try
            {
                await SendAsync();
            }
            catch (Exception ex)
            {
                Log.Error("SendAsync", ex);
                SetStatus("发送失败：" + ex.Message);
                SetBusy(false);
            }
        }

        private async Task SendAsync()
        {
            if (_busy) return;
            var model = CurrentModel();
            if (string.IsNullOrEmpty(model))
            {
                SetStatus("请先选一个模型（或到「模型管理…」拉一个）");
                return;
            }
            var text = _input.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            _input.Clear();
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            AppendUser(text);
            var historySnapshot = new List<ChatMessage>(_history);   // 不含本条
            _history.Add(new ChatMessage("user", text));

            var answer = new StringBuilder();
            var reasoningShown = false;
            GenerationStats stats = null;

            try
            {
                stats = await _services.AskAsync(
                    model,
                    historySnapshot,
                    text,
                    forceSearch: false,
                    onDelta: chunk => _ui.BeginInvoke(new Action(() => Append(chunk))),
                    onReasoning: chunk =>
                    {
                        if (!reasoningShown)
                        {
                            reasoningShown = true;
                            _ui.BeginInvoke(new Action(() => Append("\n[思考中…] ")));
                        }
                        _ui.BeginInvoke(new Action(() => Append(chunk)));
                    },
                    onStatus: s => _ui.BeginInvoke(new Action(() => SetStatus(s))),
                    ct: ct).ConfigureAwait(true);

                // 上面 onDelta 已经把正文写进输出框了，这里只需要把最终文本存进历史
                answer.Append(ExtractLastAnswer());
                if (stats != null && !string.IsNullOrEmpty(stats.Error))
                {
                    Append("\n\n[错误] " + stats.Error + "\n");
                }
                Append("\n\n");
            }
            catch (Exception ex)
            {
                Log.Error("SendAsync 内部", ex);
                Append("\n\n[异常] " + ex.Message + "\n\n");
            }
            finally
            {
                if (answer.Length > 0)
                    _history.Add(new ChatMessage("assistant", answer.ToString()));
                SetStatus(stats != null ? stats.OneLine() : "已结束");
                SetBusy(false);
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
        }

        /// <summary>从输出框里取出"这一轮"的正文（输出框是纯追加的，所以按标记切）。</summary>
        private string ExtractLastAnswer()
        {
            try
            {
                var all = _output.Text;
                var idx = all.LastIndexOf("\n【你】", StringComparison.Ordinal);
                if (idx < 0) return "";
                var tail = all.Substring(idx);
                var nl = tail.IndexOf('\n');
                return nl < 0 ? "" : tail.Substring(nl + 1).Trim();
            }
            catch { return ""; }
        }

        private void CancelCurrent()
        {
            try
            {
                if (_cts != null) { _cts.Cancel(); SetStatus("正在停止…"); }
            }
            catch (Exception ex) { Log.Error("CancelCurrent", ex); }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            try
            {
                _sendBtn.IsEnabled = !busy;
                _stopBtn.IsEnabled = busy;
                _input.IsEnabled = !busy;
                _refreshBtn.IsEnabled = !busy;
                _modelBox.IsEnabled = !busy;
            }
            catch { }
        }

        // ------------------------------------------------------------ 输出

        private void AppendUser(string text)
        {
            Append("\n【你】" + text + "\n\n【模型】");
        }

        private void Append(string text)
        {
            try
            {
                _output.AppendText(text);
                _output.ScrollToEnd();
            }
            catch (Exception ex) { Log.Error("Append", ex); }
        }

        private void SetStatus(string text)
        {
            try
            {
                _status.Text = text;
                Log.Info("状态：" + text);
            }
            catch { }
        }

        private void OnLogLine(string line)
        {
            try
            {
                _ui.BeginInvoke(new Action(() =>
                {
                    if (_logLines++ > 2000) { _logBox.Clear(); _logLines = 0; }
                    _logBox.AppendText(line + Environment.NewLine);
                    _logBox.ScrollToEnd();
                }));
            }
            catch { }
        }

        private void ClearConversation()
        {
            try
            {
                _history.Clear();
                _output.Clear();
                _services.Pipeline.ClearSearchCache();
                Append("（会话已清空，检索块缓存也已清空）\n\n");
            }
            catch (Exception ex) { Log.Error("ClearConversation", ex); }
        }

        private void SafeFire(Task t)
        {
            t.ContinueWith(x =>
            {
                if (x.Exception != null) Log.Error("后台任务", x.Exception);
            }, TaskScheduler.Default);
        }

        private void PersistSettings()
        {
            try { _services.SaveSettings(); }
            catch (Exception ex) { Log.Error("PersistSettings", ex); }
        }

        // ------------------------------------------------------------ 弹窗

        private void OpenModelManager()
        {
            try
            {
                var dlg = new ModelManagerWindow(_services) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                SafeFire(RefreshModelsAsync());
            }
            catch (Exception ex) { Log.Error("OpenModelManager", ex); MessageBox.Show(ex.Message, "模型管理"); }
        }

        private void OpenByomDialog()
        {
            try
            {
                var dlg = new ByomWindow(_services) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            catch (Exception ex) { Log.Error("OpenByomDialog", ex); MessageBox.Show(ex.Message, "BYOM"); }
        }

        private void OpenSearchTest()
        {
            try
            {
                var dlg = new SearchTestWindow(_services) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            catch (Exception ex) { Log.Error("OpenSearchTest", ex); MessageBox.Show(ex.Message, "搜索测试"); }
        }
    }
}
