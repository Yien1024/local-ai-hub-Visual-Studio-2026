using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using LocalAIHub.Core;

namespace LocalAIHub
{
    /// <summary>
    /// BYOM（VS Copilot 自带模型）配置助手。
    ///
    /// 这是本插件区别于"又一个聊天窗"的地方：它替用户写
    /// %LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json。
    /// 那个文件的 CustomURL / Endpoint 枚举是上一轮实测+反汇编才搞清楚的，
    /// 手动配很容易踩"模型出现在选择器里但请求静默消失"的坑。
    /// </summary>
    public sealed class ByomWindow : Window
    {
        private readonly HubServices _services;
        private readonly Dispatcher _ui;
        private TextBox _pathBox;
        private TextBox _urlBox;
        private ComboBox _providerBox;
        private CheckBox _selectBox;
        private TextBox _current;
        private TextBlock _status;
        private ListView _list;
        private List<OllamaModel> _models = new List<OllamaModel>();

        public ByomWindow(HubServices services)
        {
            _services = services;
            _ui = Dispatcher.CurrentDispatcher;
            Title = "Local AI Hub · 注册进 Copilot 自带模型 (BYOM)";
            Width = 860;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try
            {
                BuildUi();
                Loaded += async (s, e) => { await LoadAsync(); RefreshCurrent(); };
            }
            catch (Exception ex) { Log.Error("ByomWindow", ex); Content = new FallbackView(ex.ToString()); }
        }

        private void BuildUi()
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                Text = "把本机 Ollama 模型注册进 Visual Studio Copilot 的「自带模型」，"
                       + "之后在 Copilot Chat 的模型选择器里就能选到它们。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());

            AddRow(g, 0, "提供商名称", out _providerBox, "ollama-local");
            AddRow(g, 1, "Base URL", out _urlBox, _services.Settings.ByomBaseUrl);
            top.Children.Add(g);

            _urlBox.ToolTip = "必须指向提供 OpenAI 兼容接口的端点。\n"
                              + "直连 Ollama：http://127.0.0.1:11434/v1\n"
                              + "走你自己的联网代理：http://127.0.0.1:11435/v1（这样 Copilot 里的模型也会联网）";

            _selectBox = new CheckBox
            {
                Content = "注册后直接勾选这些模型（IsSelected=true）",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 6),
            };
            top.Children.Add(_selectBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            btnRow.Children.Add(MakeBtn("刷新模型列表", async () => await LoadAsync()));
            btnRow.Children.Add(MakeBtn("注册选中的模型", () => { Register(); return Task.FromResult(0); }));
            btnRow.Children.Add(MakeBtn("全选", () => { SetAll(true); return Task.FromResult(0); }));
            btnRow.Children.Add(MakeBtn("全不选", () => { SetAll(false); return Task.FromResult(0); }));
            top.Children.Add(btnRow);

            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            top.Children.Add(_status);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new StackPanel();
            bottom.Children.Add(new TextBlock
            {
                Text = "当前 BYOM 配置文件内容（只读）：",
                Margin = new Thickness(0, 6, 0, 2),
            });
            _pathBox = new TextBox { IsReadOnly = true, Text = ByomConfig.FilePath, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_pathBox);
            _current = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 150,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            bottom.Children.Add(_current);
            var notes = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brushes.Gray,
                Text = "提示：\n"
                       + "· 自定义模型只在 Copilot 的 Agent 模式生效；面板本身仍需要 api.githubcopilot.com 才能初始化，"
                       + "所以完全断外网的网络里 Copilot 面板会一直转圈（这不是插件的问题）。\n"
                       + "· 想让 Copilot 里的本地模型也联网：Base URL 填你本机代理的 /v1 地址（例如 11435），"
                       + "插件与代理是两条独立的路，互不依赖。\n"
                       + "· 每次写入前会自动备份成 .bak-localaihub-<时间戳>。",
            };
            bottom.Children.Add(notes);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            _list = new ListView();
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn
            {
                Header = "选",
                Width = 40,
                CellTemplate = BuildCheckTemplate(),
            });
            gv.Columns.Add(new GridViewColumn { Header = "模型", Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding("DisplayName") });
            gv.Columns.Add(new GridViewColumn { Header = "大小", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("SizeText") });
            gv.Columns.Add(new GridViewColumn { Header = "参数", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("ParameterSize") });
            _list.View = gv;
            root.Children.Add(_list);

            Content = root;
        }

        private static DataTemplate BuildCheckTemplate()
        {
            var dt = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetBinding(CheckBox.IsCheckedProperty,
                new System.Windows.Data.Binding("Selected") { Mode = System.Windows.Data.BindingMode.TwoWay });
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            dt.VisualTree = factory;
            return dt;
        }

        private static void AddRow(Grid g, int row, string label, out ComboBox box, string initial)
        {
            box = new ComboBox { IsEditable = true, Text = initial, Margin = new Thickness(0, 2, 0, 2) };
            box.Items.Add("ollama-local");
            box.Items.Add("ollama-proxy");
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 4, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            g.Children.Add(l); g.Children.Add(box);
        }

        private static void AddRow(Grid g, int row, string label, out TextBox box, string initial)
        {
            box = new TextBox { Text = initial, Margin = new Thickness(0, 2, 0, 2) };
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 4, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            g.Children.Add(l); g.Children.Add(box);
        }

        private Button MakeBtn(string text, Func<Task> action)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            b.Click += async (s, e) =>
            {
                try { await action(); }
                catch (Exception ex) { Log.Error("BYOM 按钮 " + text, ex); _status.Text = "出错：" + ex.Message; }
            };
            return b;
        }

        private async Task LoadAsync()
        {
            try
            {
                _status.Text = "正在读取 Ollama 模型列表…";
                _models = await _services.ListModelsAsync(CancellationToken.None).ConfigureAwait(true);
                RefreshList();
                _status.Text = "Ollama 里共 " + _models.Count + " 个模型。";
            }
            catch (Exception ex)
            {
                Log.Error("ByomWindow.LoadAsync", ex);
                _status.Text = "读取模型失败：" + ex.Message;
            }
        }

        private sealed class Row
        {
            public bool Selected { get; set; }
            public string DisplayName { get; set; }
            public string SizeText { get; set; }
            public string ParameterSize { get; set; }
        }

        private void RefreshList()
        {
            _list.Items.Clear();
            foreach (var m in _models)
            {
                _list.Items.Add(new Row
                {
                    Selected = true,
                    DisplayName = m.DisplayName,
                    SizeText = m.SizeText,
                    ParameterSize = m.ParameterSize,
                });
            }
        }

        private void SetAll(bool value)
        {
            foreach (var item in _list.Items) ((Row)item).Selected = value;
            _list.Items.Refresh();
        }

        private void Register()
        {
            try
            {
                var spec = new ByomConfig.ProviderSpec
                {
                    Name = _providerBox.Text.Trim(),
                    BaseUrl = _urlBox.Text.Trim(),
                    Endpoint = ByomConfig.EndpointOllama,
                };
                if (spec.BaseUrl.Length == 0)
                {
                    _status.Text = "Base URL 不能为空 —— 留空时 VS 会把请求静默丢掉（实测过的坑）。";
                    return;
                }
                var picked = _list.Items.Cast<Row>().Where(r => r.Selected).ToList();
                if (picked.Count == 0) { _status.Text = "一个模型都没选。"; return; }
                foreach (var r in picked)
                {
                    spec.Models.Add(new ByomConfig.ModelSpec
                    {
                        Id = r.DisplayName,
                        DisplayName = r.DisplayName + "（本地）",
                        ToolCalling = true,
                        MaxInputTokens = Math.Min(_services.Settings.ContextLength, 61440),
                        MaxOutputTokens = _services.Settings.MaxOutputTokens,
                    });
                }
                string err;
                var backup = ByomConfig.Register(spec, _selectBox.IsChecked == true, out err);
                if (backup == null)
                {
                    _status.Text = "注册失败：" + err;
                }
                else
                {
                    _status.Text = "已注册 " + picked.Count + " 个模型到提供商 \"" + spec.Name + "\"。"
                                   + (string.IsNullOrEmpty(backup) ? "（原文件不存在，未产生备份）" : "（备份：" + System.IO.Path.GetFileName(backup) + "）")
                                   + "  重启 VS 后生效。";
                    _services.Settings.ByomBaseUrl = spec.BaseUrl;
                    _services.SaveSettings();
                }
                RefreshCurrent();
            }
            catch (Exception ex)
            {
                Log.Error("ByomWindow.Register", ex);
                _status.Text = "注册异常：" + ex.Message;
            }
        }

        private void RefreshCurrent()
        {
            try { _current.Text = ByomConfig.Describe(); }
            catch (Exception ex) { _current.Text = "读取失败：" + ex.Message; }
        }
    }

    /// <summary>搜索测试：不经过模型，直接看引擎返回什么、闸门留下什么。</summary>
    public sealed class SearchTestWindow : Window
    {
        private readonly HubServices _services;
        private TextBox _query;
        private TextBox _result;
        private TextBlock _status;
        private Button _goBtn;

        public SearchTestWindow(HubServices services)
        {
            _services = services;
            Title = "Local AI Hub · 联网搜索测试（不经过模型）";
            Width = 820;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try { BuildUi(); }
            catch (Exception ex) { Log.Error("SearchTestWindow", ex); Content = new FallbackView(ex.ToString()); }
        }

        private void BuildUi()
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock { Text = "查询:", Margin = new Thickness(0, 4, 6, 0) });
            _query = new TextBox { Width = 480, Text = "Python报错 ModuleNotFoundError: No module named requests 怎么解决" };
            top.Children.Add(_query);
            _goBtn = new Button { Content = "搜索", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            _goBtn.Click += async (s, e) => await GoAsync();
            top.Children.Add(_goBtn);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            _status = new TextBlock { Text = "就绪。能直接看到：抽出来的检索词、用了哪个引擎、过闸门后剩下几条。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(_status, Dock.Top);
            root.Children.Add(_status);

            _result = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft YaHei UI, Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            root.Children.Add(_result);
            Content = root;
        }

        private async Task GoAsync()
        {
            try
            {
                _goBtn.IsEnabled = false;
                _status.Text = "搜索中…（本机网络故障时会明确报错，不会假装成功）";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var text = await _services.SearchOnceAsync(_query.Text, CancellationToken.None).ConfigureAwait(true);
                _status.Text = "耗时 " + sw.Elapsed.TotalSeconds.ToString("0.00") + " 秒";
                _result.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("SearchTestWindow.GoAsync", ex);
                _status.Text = "失败：" + ex.Message;
            }
            finally { _goBtn.IsEnabled = true; }
        }
    }
}
