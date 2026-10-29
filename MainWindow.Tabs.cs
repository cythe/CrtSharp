using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CrtSharp;

public partial class MainWindow
{
    private Point _tabDragStart;
    private TabItem? _draggedTab;
    private bool _dragCancelled;
    private bool _windowCloseConfirmed;

    private TabItem? FindTab(ActiveSession active) =>
        _activeSessions.FirstOrDefault(pair => ReferenceEquals(pair.Value, active)).Key;

    private void RenameCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TabItem tab)
        {
            RenameTab(tab);
        }
        else
        {
            StatusText.Text = "请先打开或选择一个终端标签";
        }
    }

    private void RenameTab(TabItem tab)
    {
        if (!_activeSessions.TryGetValue(tab, out var active))
        {
            return;
        }

        var dialog = new RenameTabDialog(active.Profile.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        active.Profile.Name = dialog.TabName;
        SetTabTitle(tab, dialog.TabName);
        StatusText.Text = $"标签已重命名为：{dialog.TabName}";
    }

    private static void SetTabTitle(TabItem tab, string titleText)
    {
        if (tab.Header is not Panel panel)
        {
            return;
        }

        var title = panel.Children
            .OfType<TextBlock>()
            .FirstOrDefault(element => Equals(element.Tag, "TabTitle"));
        if (title is not null)
        {
            title.Text = titleText;
        }
    }

    private void UpdateTabConnectionState(TabItem tab, string status)
    {
        if (tab.Header is not Panel panel)
        {
            return;
        }

        var indicator = panel.Children
            .OfType<Border>()
            .FirstOrDefault(element => Equals(element.Tag, "ConnectionState"));
        if (indicator is null)
        {
            return;
        }

        indicator.Background = status switch
        {
            "已连接" => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
            var value when value.Contains("正在", StringComparison.Ordinal) =>
                new SolidColorBrush(Color.FromRgb(220, 220, 170)),
            var value when value.Contains("错误", StringComparison.Ordinal) ||
                           value.Contains("断开", StringComparison.Ordinal) ||
                           value.Contains("取消", StringComparison.Ordinal) =>
                new SolidColorBrush(Color.FromRgb(241, 76, 76)),
            _ => new SolidColorBrush(Color.FromRgb(136, 136, 136))
        };
        indicator.ToolTip = status;
    }

    private bool ConfirmCloseTab(TabItem tab)
    {
        if (!_activeSessions.TryGetValue(tab, out var active) ||
            (!active.Connection.IsConnected && !active.IsConnecting))
        {
            return true;
        }

        return MessageBox.Show(
            this,
            $"会话“{active.Profile.Name}”仍在{(active.IsConnecting ? "连接" : "运行")}。\n确定要断开并关闭标签吗？",
            "关闭活动会话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_windowCloseConfirmed)
        {
            return;
        }

        var activeCount = _activeSessions.Values.Count(active =>
            active.Connection.IsConnected || active.IsConnecting);
        if (activeCount == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"仍有 {activeCount} 个活动会话。\n确定要全部断开并退出吗？",
            "退出 CrtSharp",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _windowCloseConfirmed = true;
    }

    private void TerminalTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 不限制标签数量：允许拖动窗口里最后一个标签（跨窗口移动 / detach）。

        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<Button>(source) is not null)
        {
            _draggedTab = null;
            return;
        }

        var tab = FindVisualParent<TabItem>(source);
        if (tab is null || !_activeSessions.ContainsKey(tab))
        {
            _draggedTab = null;
            return;
        }

        _tabDragStart = e.GetPosition(TerminalTabs);
        _draggedTab = tab;
    }

    private void TerminalTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTab is null)
        {
            return;
        }

        var current = e.GetPosition(TerminalTabs);
        if (Math.Abs(current.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tab = _draggedTab;
        _draggedTab = null;
        _dragCancelled = false;
        try
        {
            var result = DragDrop.DoDragDrop(
                TerminalTabs,
                new DataObject(typeof(TabItem), tab),
                DragDropEffects.Move);

            if (result == DragDropEffects.None &&
                !_dragCancelled &&
                _activeSessions.ContainsKey(tab) &&
                !IsInTabStrip(Mouse.GetPosition(TerminalTabs)))
            {
                TearOffTab(tab);
            }
        }
        finally
        {
            Mouse.SetCursor(Cursors.Arrow);
        }
    }

    private void TerminalTabs_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.SizeAll);
        e.Handled = true;
    }

    private void TerminalTabs_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragCancelled = true;
        }
    }

    private void TerminalTabs_DragOver(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(TabItem)) as TabItem;
        e.Effects = source is not null &&
                    TryResolveDraggedSession(source, out _, out _) &&
                    IsInTabStrip(e.GetPosition(TerminalTabs))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>解析拖动中的 TabItem：允许来自本窗口或其他 CrtSharp 窗口。</summary>
    private bool TryResolveDraggedSession(TabItem source, out MainWindow? ownerWindow, out ActiveSession? active)
    {
        ownerWindow = Window.GetWindow(source) as MainWindow;
        if (ownerWindow is not null &&
            ownerWindow._activeSessions.TryGetValue(source, out var session) &&
            !session.IsClosing && !session.IsTransferring)
        {
            active = session;
            return true;
        }
        active = null;
        return false;
    }

    private void TerminalTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TabItem)) is not TabItem source ||
            !TryResolveDraggedSession(source, out var ownerWindow, out var active) ||
            ownerWindow is null || active is null ||
            !IsInTabStrip(e.GetPosition(TerminalTabs)))
        {
            return;
        }

        var target = FindVisualParent<TabItem>(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(ownerWindow, this))
        {
            MoveTabWithinWindow(source, target);
        }
        else
        {
            AdoptTabFromWindow(ownerWindow, source, active, target);
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>本窗口内拖动重排。</summary>
    private void MoveTabWithinWindow(TabItem source, TabItem? target)
    {
        var sourceIndex = TerminalTabs.Items.IndexOf(source);
        var targetIndex = target is null
            ? TerminalTabs.Items.Count
            : TerminalTabs.Items.IndexOf(target) +
              (Mouse.GetPosition(target).X > target.ActualWidth / 2 ? 1 : 0);

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }
        targetIndex = Math.Clamp(targetIndex, 0, TerminalTabs.Items.Count - 1);
        if (sourceIndex != targetIndex)
        {
            TerminalTabs.Items.Remove(source);
            TerminalTabs.Items.Insert(targetIndex, source);
            TerminalTabs.SelectedItem = source;
        }
    }

    /// <summary>把其他窗口的标签收进来；源窗口空了就关闭回收。</summary>
    private void AdoptTabFromWindow(MainWindow sourceWindow, TabItem sourceTab, ActiveSession active, TabItem? target)
    {
        active.IsTransferring = true;
        try
        {
            sourceWindow._activeSessions.Remove(sourceTab);
            sourceTab.Content = null;
            sourceWindow.TerminalTabs.Items.Remove(sourceTab);
            sourceWindow.TransferLoggingTo(active.Terminal, this);

            active.Owner = this;
            var adoptedTab = new TabItem();
            adoptedTab.Content = active.Terminal;
            adoptedTab.Header = CreateTabHeader(adoptedTab, active.Profile.Name);
            _activeSessions.Add(adoptedTab, active);

            var insertIndex = target is null
                ? TerminalTabs.Items.Count
                : Math.Clamp(TerminalTabs.Items.IndexOf(target) +
                    (Mouse.GetPosition(target).X > target.ActualWidth / 2 ? 1 : 0),
                    0, TerminalTabs.Items.Count);
            TerminalTabs.Items.Insert(insertIndex, adoptedTab);
            TerminalTabs.SelectedItem = adoptedTab;
            EmptyState.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{active.Profile.Name} — {active.Status}";
            TerminalSizeText.Text = $"{active.Columns} × {active.Rows}";
            UpdateTabConnectionState(adoptedTab, active.Status);

            sourceWindow.RefreshTabHostUi();
            active.Terminal.RebindToWindow(this);
            _ = active.Terminal.FocusTerminalAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"接收标签失败：{exception.Message}";
        }
        finally
        {
            active.IsTransferring = false;
        }

        // 源窗口已无标签：延迟关闭，等拖放栈帧退出后回收其资源
        //（Window_Closed 会释放该窗口名下残余的连接与终端）。
        if (sourceWindow.TerminalTabs.Items.Count == 0)
        {
            sourceWindow._windowCloseConfirmed = true;
            sourceWindow.Dispatcher.BeginInvoke(() => sourceWindow.Close());
        }
    }

    private bool IsInTabStrip(Point point)
    {
        var headerHeight = TerminalTabs.Items
            .OfType<TabItem>()
            .Select(tab => tab.ActualHeight)
            .DefaultIfEmpty(32)
            .Max();
        return point.X >= 0 && point.X <= TerminalTabs.ActualWidth &&
               point.Y >= 0 && point.Y <= Math.Max(32, headerHeight + 4);
    }

    private void TearOffTab(TabItem tab)
    {
        if (TerminalTabs.Items.Count < 1 ||
            !_activeSessions.TryGetValue(tab, out var active) ||
            active.IsClosing || active.IsTransferring)
        {
            return;
        }

        active.IsTransferring = true;
        var sourceIndex = TerminalTabs.Items.IndexOf(tab);
        var destination = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(MinWidth, ActualWidth * 0.85),
            Height = Math.Max(MinHeight, ActualHeight * 0.85)
        };
        var destinationTab = new TabItem();
        PositionTearOffWindow(destination);

        try
        {
            _activeSessions.Remove(tab);
            tab.Content = null;
            TerminalTabs.Items.Remove(tab);
            TransferLoggingTo(active.Terminal, destination);

            active.Owner = destination;
            destinationTab.Content = active.Terminal;
            destinationTab.Header = destination.CreateTabHeader(destinationTab, active.Profile.Name);
            destination._activeSessions.Add(destinationTab, active);
            destination.TerminalTabs.Items.Add(destinationTab);
            destination.TerminalTabs.SelectedItem = destinationTab;
            destination.EmptyState.Visibility = Visibility.Collapsed;
            destination.StatusText.Text = $"{active.Profile.Name} — {active.Status}";
            destination.TerminalSizeText.Text = $"{active.Columns} × {active.Rows}";
            destination.UpdateTabConnectionState(destinationTab, active.Status);

            RefreshTabHostUi();
            if (TerminalTabs.Items.Count == 0)
            {
                // 拖走的是最后一个标签：老窗口已空，延迟关闭回收资源。
                _windowCloseConfirmed = true;
                Dispatcher.BeginInvoke(Close);
            }
            destination.Show();
            destination.Activate();
            active.Terminal.RebindToWindow(destination);
            _ = active.Terminal.FocusTerminalAsync();
        }
        catch (Exception exception)
        {
            if (destination._activeSessions.Remove(destinationTab))
            {
                destinationTab.Content = null;
                destination.TerminalTabs.Items.Remove(destinationTab);
            }
            destination.TransferLoggingTo(active.Terminal, this);
            active.Owner = this;
            tab.Content = active.Terminal;
            tab.Header = CreateTabHeader(tab, active.Profile.Name);
            if (!_activeSessions.ContainsKey(tab))
            {
                _activeSessions.Add(tab, active);
                var restoreIndex = Math.Clamp(sourceIndex, 0, TerminalTabs.Items.Count);
                TerminalTabs.Items.Insert(restoreIndex, tab);
                TerminalTabs.SelectedItem = tab;
            }
            UpdateTabConnectionState(tab, active.Status);
            destination.Close();
            StatusText.Text = $"无法创建新窗口：{exception.Message}";
        }
        finally
        {
            active.IsTransferring = false;
        }
    }

    private void PositionTearOffWindow(Window destination)
    {
        var screenPoint = PointToScreen(Mouse.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            screenPoint = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }

        destination.Left = Math.Max(SystemParameters.VirtualScreenLeft, screenPoint.X - 120);
        destination.Top = Math.Max(SystemParameters.VirtualScreenTop, screenPoint.Y - 18);
    }

    private void RefreshTabHostUi()
    {
        EmptyState.Visibility = TerminalTabs.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (TerminalTabs.Items.Count > 0 && TerminalTabs.SelectedItem is null)
        {
            TerminalTabs.SelectedIndex = 0;
        }
        if (TerminalTabs.SelectedItem is TabItem selected &&
            _activeSessions.TryGetValue(selected, out var active))
        {
            StatusText.Text = $"{active.Profile.Name} — {active.Status}";
            TerminalSizeText.Text = $"{active.Columns} × {active.Rows}";
        }
        else
        {
            StatusText.Text = "就绪";
            TerminalSizeText.Text = "-- × --";
        }
    }
}
