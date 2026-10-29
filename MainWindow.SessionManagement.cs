using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrtSharp.Models;

namespace CrtSharp;

public partial class MainWindow
{
    private Point _sessionDragStart;
    private SessionProfile? _draggedProfile;

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionProfile profile)
        {
            StatusText.Text = "请先选择要编辑的会话";
            return;
        }

        var dialog = new NewSessionDialog(profile, editOnly: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultProfile is null)
        {
            return;
        }

        SaveProfile(dialog.ResultProfile);
        SessionList.SelectedItem = _profiles.FirstOrDefault(item => item.Id == profile.Id);
        StatusText.Text = $"已保存会话：{dialog.ResultProfile.Name}";
    }

    private async void DuplicateCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not TabItem tab ||
            !_activeSessions.TryGetValue(tab, out var active))
        {
            StatusText.Text = "请先打开或选择一个终端标签";
            return;
        }
        if (active.Profile.Protocol == ConnectionProtocol.Serial)
        {
            StatusText.Text = "串口会话独占物理端口，不能复制标签";
            return;
        }

        await DuplicateTabAsync(tab);
    }

    private async Task DuplicateTabAsync(TabItem sourceTab)
    {
        if (!_activeSessions.TryGetValue(sourceTab, out var source))
        {
            return;
        }
        if (source.Profile.Protocol == ConnectionProtocol.Serial)
        {
            StatusText.Text = "串口会话独占物理端口，不能复制标签";
            return;
        }

        StatusText.Text = $"正在复制会话：{source.Profile.Name}";
        await OpenSessionAsync(source.Profile.Clone(), source.Password);
    }

    private ContextMenu CreateTabContextMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        var textOnlyStyle = (Style)FindResource("TextOnlyContextMenuItemStyle");
        MenuItem CreateItem(string header) => new() { Header = header, Style = textOnlyStyle };

        var rename = CreateItem("重命名标签…");
        rename.Click += (_, _) => RenameTab(tab);
        menu.Items.Add(rename);

        var duplicate = CreateItem("复制会话到新标签");
        duplicate.Click += async (_, _) => await DuplicateTabAsync(tab);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());

        var toggleLog = CreateItem(string.Empty);
        toggleLog.Click += async (_, _) => await ToggleLoggingAsync(tab);
        menu.Items.Add(toggleLog);

        var openLogs = CreateItem("打开日志目录");
        openLogs.Click += (_, _) => OpenLogDirectory();
        menu.Items.Add(openLogs);
        menu.Items.Add(new Separator());

        var appearance = CreateItem("终端外观…");
        appearance.Click += async (_, _) =>
        {
            if (_activeSessions.TryGetValue(tab, out var active))
            {
                await EditTerminalAppearanceAsync(active);
            }
        };
        menu.Items.Add(appearance);
        menu.Items.Add(new Separator());

        var disconnect = CreateItem("断开");
        disconnect.Click += async (_, _) =>
        {
            if (_activeSessions.TryGetValue(tab, out var active))
            {
                active.ConnectionCancellation.Cancel();
                await active.Connection.DisconnectAsync();
            }
        };
        menu.Items.Add(disconnect);

        var close = CreateItem("关闭标签");
        close.Click += async (_, _) => await CloseTabAsync(tab);
        menu.Items.Add(close);

        menu.Opened += (_, _) =>
        {
            toggleLog.Header = IsLogging(tab) ? "停止记录日志" : "开始记录日志";
            duplicate.IsEnabled = _activeSessions.TryGetValue(tab, out var active) &&
                                  active.Profile.Protocol != ConnectionProtocol.Serial;
        };
        return menu;
    }

    private void SessionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedProfile = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as SessionProfile;
        _sessionDragStart = e.GetPosition(SessionList);
    }

    private void SessionList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedProfile is null)
        {
            return;
        }

        var current = e.GetPosition(SessionList);
        if (Math.Abs(current.X - _sessionDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _sessionDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var profile = _draggedProfile;
        _draggedProfile = null;
        DragDrop.DoDragDrop(SessionList, new DataObject(typeof(SessionProfile), profile), DragDropEffects.Move);
    }

    private void SessionList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(SessionProfile)) is SessionProfile
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SessionList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SessionProfile)) is not SessionProfile source)
        {
            return;
        }

        var sourceIndex = _profiles.IndexOf(source);
        if (sourceIndex < 0)
        {
            return;
        }

        var target = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetIndex = target?.DataContext is SessionProfile targetProfile
            ? _profiles.IndexOf(targetProfile) +
              (e.GetPosition(target).Y > target.ActualHeight / 2 ? 1 : 0)
            : _profiles.Count;
        if (targetIndex < 0)
        {
            targetIndex = _profiles.Count;
        }

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }
        targetIndex = Math.Clamp(targetIndex, 0, _profiles.Count - 1);
        if (sourceIndex != targetIndex)
        {
            _profiles.Move(sourceIndex, targetIndex);
            _sessionStore.Save(_profiles);
            SessionList.SelectedItem = source;
            StatusText.Text = $"已调整会话顺序：{source.Name}";
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SessionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        SessionList.SelectedItem = item?.DataContext;
    }

    private void SessionContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        SessionContextMenu.IsEnabled = SessionList.SelectedItem is SessionProfile;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
