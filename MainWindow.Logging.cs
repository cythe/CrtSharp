using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CrtSharp.Controls;
using CrtSharp.Services;

namespace CrtSharp;

public partial class MainWindow
{
    private readonly Dictionary<TerminalView, TerminalLogWriter> _logWriters = [];

    private async void StartLogging_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TabItem tab)
        {
            await StartLoggingAsync(tab);
        }
        else
        {
            StatusText.Text = "请先打开或选择一个终端标签";
        }
    }

    private async void StopLogging_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TabItem tab)
        {
            await StopLoggingAsync(tab, showNotice: true);
        }
    }

    private async void ToggleLogging_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not TabItem tab)
        {
            StatusText.Text = "请先打开或选择一个终端标签";
            return;
        }

        await ToggleLoggingAsync(tab);
    }

    private void OpenLogDirectory_Click(object sender, RoutedEventArgs e) => OpenLogDirectory();

    private async Task ToggleLoggingAsync(TabItem tab)
    {
        if (!_activeSessions.TryGetValue(tab, out var active))
        {
            return;
        }

        if (_logWriters.ContainsKey(active.Terminal))
        {
            await StopLoggingAsync(tab, showNotice: true);
        }
        else
        {
            await StartLoggingAsync(tab);
        }
    }

    private async Task StartLoggingAsync(TabItem tab)
    {
        if (!_activeSessions.TryGetValue(tab, out var active) ||
            _logWriters.ContainsKey(active.Terminal))
        {
            return;
        }

        try
        {
            var writer = new TerminalLogWriter(active.Profile.Name);
            _logWriters.Add(active.Terminal, writer);
            active.Terminal.OutputWritten += TerminalOnOutputWritten;
            active.Terminal.Disposed += TerminalOnDisposed;
            StatusText.Text = $"正在记录日志：{writer.FilePath}";
            await active.Terminal.WriteTextAsync(
                $"\r\n\u001b[90m[日志记录已开始：{writer.FilePath}]\u001b[0m\r\n");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法开始日志记录：{exception.Message}";
        }
    }

    private async Task StopLoggingAsync(TabItem tab, bool showNotice)
    {
        if (!_activeSessions.TryGetValue(tab, out var active) ||
            !_logWriters.Remove(active.Terminal, out var writer))
        {
            return;
        }

        active.Terminal.OutputWritten -= TerminalOnOutputWritten;
        active.Terminal.Disposed -= TerminalOnDisposed;
        writer.Dispose();
        StatusText.Text = $"日志已保存：{writer.FilePath}";
        if (showNotice && !active.IsClosing)
        {
            await active.Terminal.WriteTextAsync(
                $"\r\n\u001b[90m[日志已保存：{writer.FilePath}]\u001b[0m\r\n");
        }
    }

    private bool IsLogging(TabItem tab) =>
        _activeSessions.TryGetValue(tab, out var active) &&
        _logWriters.ContainsKey(active.Terminal);

    private void TerminalOnOutputWritten(object? sender, TerminalOutputEventArgs args)
    {
        if (sender is not TerminalView terminal ||
            !_logWriters.TryGetValue(terminal, out var writer))
        {
            return;
        }

        try
        {
            writer.Write(args.Data);
        }
        catch (Exception exception)
        {
            _logWriters.Remove(terminal);
            terminal.OutputWritten -= TerminalOnOutputWritten;
            terminal.Disposed -= TerminalOnDisposed;
            writer.Dispose();
            StatusText.Text = $"日志写入已停止：{exception.Message}";
        }
    }

    private void TerminalOnDisposed(object? sender, EventArgs e)
    {
        if (sender is not TerminalView terminal ||
            !_logWriters.Remove(terminal, out var writer))
        {
            return;
        }

        terminal.OutputWritten -= TerminalOnOutputWritten;
        terminal.Disposed -= TerminalOnDisposed;
        writer.Dispose();
    }

    private void TransferLoggingTo(TerminalView terminal, MainWindow destination)
    {
        if (!_logWriters.Remove(terminal, out var writer))
        {
            return;
        }

        terminal.OutputWritten -= TerminalOnOutputWritten;
        terminal.Disposed -= TerminalOnDisposed;
        destination._logWriters.Add(terminal, writer);
        terminal.OutputWritten += destination.TerminalOnOutputWritten;
        terminal.Disposed += destination.TerminalOnDisposed;
    }

    private static void OpenLogDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{TerminalLogWriter.LogsRoot}\"",
            UseShellExecute = true
        });
    }
}
