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
    /// <summary>出错时的兜底界面（不依赖任何服务，纯文本）。</summary>
    public sealed class FallbackView : UserControl
    {
        public FallbackView(string message)
        {
            Content = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Text = message,
                Margin = new Thickness(8),
                FontFamily = new FontFamily("Consolas, Courier New"),
            };
        }
    }

    /// <summary>模型管理：列出/拉取/删除/常驻显存。所有网络操作都在后台线程。</summary>
    public sealed class ModelManagerWindow : Window
    {
        private readonly HubServices _services;
        private readonly Dispatcher _ui;
        private ListView _list;
        private TextBox _pullBox;
        private TextBlock _progress;
        private ProgressBar _bar;
        private Button _pullBtn;
        private CancellationTokenSource _pullCts;
        private TextBox _runningBox;

        public ModelManagerWindow(HubServices services)
        {
            _services = services;
            _ui = Dispatcher.CurrentDispatcher;
            Title = "Local AI Hub · 模型管理";
            Width = 820;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try { BuildUi(); Loaded += async (s, e) => await RefreshAsync(); }
            catch (Exception ex) { Log.Error("ModelManagerWindow", ex); Content = new FallbackView(ex.ToString()); }
        }

        private void BuildUi()
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };

            // 顶部：拉取
            var top = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "拉取模型:", Margin = new Thickness(0, 4, 6, 0) });
            _pullBox = new TextBox { Width = 340, Text = "smtek/Qwen3.8-27B:Q4_K_M", ToolTip = "Ollama 库里的模型名，例如 qwen3-coder:30b 或 smtek/Qwen3.8-27B:Q4_K_M" };
            row.Children.Add(_pullBox);
            _pullBtn = new Button { Content = "开始拉取", Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            _pullBtn.Click += async (s, e) => await PullAsync();
            row.Children.Add(_pullBtn);
            var cancelBtn = new Button { Content = "取消", Padding = new Thickness(10, 4, 10, 4) };
            cancelBtn.Click += (s, e) => { try { if (_pullCts != null) _pullCts.Cancel(); } catch { } };
            row.Children.Add(cancelBtn);
            top.Children.Add(row);

            _bar = new ProgressBar { Height = 16, Margin = new Thickness(0, 6, 0, 2), Minimum = 0, Maximum = 100 };
            top.Children.Add(_bar);
            _progress = new TextBlock { Text = "空闲", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
            top.Children.Add(_progress);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // 底部：运行中 + 按钮
            var bottom = new StackPanel { Orientation = Orientation.Vertical };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            btnRow.Children.Add(MakeBtn("刷新", async () => await RefreshAsync()));
            btnRow.Children.Add(MakeBtn("预加载到显存（常驻）", async () => await KeepAliveAsync(true)));
            btnRow.Children.Add(MakeBtn("从显存卸载", async () => await KeepAliveAsync(false)));
            btnRow.Children.Add(MakeBtn("删除选中模型", async () => await DeleteAsync()));
            bottom.Children.Add(btnRow);
            _runningBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 52,
                Margin = new Thickness(0, 6, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
                ToolTip = "ollama ps 的结果。PROCESSOR 必须是 100% GPU，出现混合就是有层掉到内存里了，会明显变慢。",
            };
            bottom.Children.Add(_runningBox);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            _list = new ListView { Margin = new Thickness(0, 4, 0, 4) };
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "模型", Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            gv.Columns.Add(new GridViewColumn { Header = "大小", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Size") });
            gv.Columns.Add(new GridViewColumn { Header = "参数", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding("Params") });
            gv.Columns.Add(new GridViewColumn { Header = "量化", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Quant") });
            gv.Columns.Add(new GridViewColumn { Header = "修改时间", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Modified") });
            _list.View = gv;
            root.Children.Add(_list);

            Content = root;
        }

        private Button MakeBtn(string text, Func<Task> action)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            b.Click += async (s, e) =>
            {
                try { await action(); }
                catch (Exception ex) { Log.Error("模型管理按钮 " + text, ex); MessageBox.Show(ex.Message, text); }
            };
            return b;
        }

        private async Task RefreshAsync()
        {
            try
            {
                _progress.Text = "正在读取…";
                var models = await _services.ListModelsAsync(CancellationToken.None).ConfigureAwait(true);
                _list.Items.Clear();
                foreach (var m in models)
                {
                    _list.Items.Add(new
                    {
                        Name = m.DisplayName,
                        Size = m.SizeText,
                        Params = m.ParameterSize,
                        Quant = m.QuantizationLevel,
                        Modified = m.ModifiedAt == DateTimeOffset.MinValue ? "?" : m.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    });
                }
                var running = await _services.ListRunningAsync(CancellationToken.None).ConfigureAwait(true);
                if (running.Count == 0) _runningBox.Text = "当前没有模型常驻显存（ollama ps 为空）";
                else _runningBox.Text = string.Join(Environment.NewLine, running.Select(r =>
                    r.Name + "   " + r.SizeText + "   " + r.PlacementText + "   上下文 " + r.ContextLength
                    + (r.FullyOnGpu ? "   ✓ 全部在显存" : "   ⚠ 有层在内存，会明显变慢")));
                _progress.Text = models.Count + " 个模型";
            }
            catch (Exception ex)
            {
                Log.Error("ModelManager.RefreshAsync", ex);
                _progress.Text = "读取失败：" + ex.Message;
            }
        }

        private async Task PullAsync()
        {
            var tag = _pullBox.Text.Trim();
            if (tag.Length == 0) return;
            _pullCts = new CancellationTokenSource();
            _pullBtn.IsEnabled = false;
            var started = DateTime.Now;
            try
            {
                var ok = await _services.PullModelAsync(tag, (status, done, total) =>
                {
                    _ui.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (total > 0)
                            {
                                var pct = done * 100.0 / total;
                                _bar.Value = pct;
                                var speed = (DateTime.Now - started).TotalSeconds > 0
                                    ? done / 1024.0 / 1024.0 / Math.Max(1, (DateTime.Now - started).TotalSeconds)
                                    : 0;
                                _progress.Text = status + "  " + pct.ToString("0.0") + "%  "
                                                 + (done / 1073741824.0).ToString("0.00") + "/"
                                                 + (total / 1073741824.0).ToString("0.00") + " GB  "
                                                 + speed.ToString("0.0") + " MB/s";
                            }
                            else _progress.Text = status;
                        }
                        catch { }
                    }));
                }, _pullCts.Token).ConfigureAwait(true);

                _progress.Text = ok ? ("完成：" + tag) : ("未完成：" + tag + "（可重试，会断点续传）");
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Log.Error("PullAsync", ex);
                _progress.Text = "拉取出错：" + ex.Message;
            }
            finally
            {
                _pullBtn.IsEnabled = true;
                try { _pullCts.Dispose(); } catch { }
                _pullCts = null;
            }
        }

        private async Task KeepAliveAsync(bool keepForever)
        {
            var name = SelectedName();
            if (name == null) { _progress.Text = "先在列表里选一个模型"; return; }
            _progress.Text = "正在" + (keepForever ? "预加载" : "卸载") + " " + name + " …";
            var result = await _services.SetKeepAliveAsync(name, keepForever, CancellationToken.None)
                .ConfigureAwait(true);
            _progress.Text = result;
            await RefreshAsync();
        }

        private async Task DeleteAsync()
        {
            var name = SelectedName();
            if (name == null) { _progress.Text = "先在列表里选一个模型"; return; }
            if (MessageBox.Show("确定删除模型 " + name + " ？\n（会从磁盘移除权重，需要重新拉取）", "删除模型",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var ok = await _services.DeleteModelAsync(name, CancellationToken.None).ConfigureAwait(true);
            _progress.Text = ok ? ("已删除 " + name) : ("删除失败 " + name + "（看日志）");
            await RefreshAsync();
        }

        private string SelectedName()
        {
            var item = _list.SelectedItem;
            if (item == null) return null;
            var prop = item.GetType().GetProperty("Name");
            return prop == null ? null : Convert.ToString(prop.GetValue(item, null));
        }
    }
}
