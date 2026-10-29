using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CrtSharp.Models;
using Microsoft.Web.WebView2.Core;

namespace CrtSharp.Controls;

public sealed class TerminalInputEventArgs(string data) : EventArgs
{
    public string Data { get; } = data;
}

public sealed class TerminalResizeEventArgs(int columns, int rows) : EventArgs
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
}

public sealed class TerminalOutputEventArgs(byte[] data) : EventArgs
{
    public byte[] Data { get; } = data;
}

public partial class TerminalView : UserControl, IDisposable
{
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _scrollbackLines;
    private readonly TerminalAppearanceSettings _initialAppearance;
    private Task? _initializeTask;
    private bool _disposed;

    public TerminalView(SessionProfile profile)
    {
        _scrollbackLines = Math.Clamp(profile.ScrollbackLines, 0, 1_000_000);
        _initialAppearance = profile.GetTerminalAppearance();
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    public event EventHandler<TerminalInputEventArgs>? InputReceived;
    public event EventHandler<TerminalResizeEventArgs>? TerminalResized;
    public event EventHandler<TerminalOutputEventArgs>? OutputWritten;
    public event EventHandler? Disposed;

    public Task InitializeAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return _initializeTask ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        await Browser.EnsureCoreWebView2Async();
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var assets = Path.Combine(AppContext.BaseDirectory, "TerminalAssets");
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "crtsharp.assets",
            assets,
            CoreWebView2HostResourceAccessKind.DenyCors);
        Browser.Source = new Uri("https://crtsharp.assets/terminal.html");
        await _ready.Task;
        await Browser.CoreWebView2.ExecuteScriptAsync(
            $"window.crtSetScrollback({_scrollbackLines})");
        await ApplyAppearanceCoreAsync(_initialAppearance);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (_disposed)
        {
            return;
        }

        await InitializeAsync();
        var bytes = data.ToArray();
        var base64 = Convert.ToBase64String(bytes);
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.crtWrite('{base64}')");
        OutputWritten?.Invoke(this, new TerminalOutputEventArgs(bytes));
    }

    public Task WriteTextAsync(string text) => WriteAsync(Encoding.UTF8.GetBytes(text));

    public async Task ApplyAppearanceAsync(TerminalAppearanceSettings settings)
    {
        await InitializeAsync();
        await ApplyAppearanceCoreAsync(settings);
    }

    private Task ApplyAppearanceCoreAsync(TerminalAppearanceSettings settings)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fontFamily = settings.FontFamily,
            fontSize = Math.Clamp(settings.FontSize, 8, 72),
            theme = settings.Theme.ToString(),
            cursorStyle = settings.CursorStyle.ToString().ToLowerInvariant(),
            cursorBlink = settings.CursorBlink
        });
        return Browser.CoreWebView2.ExecuteScriptAsync($"window.crtApplyAppearance({payload})");
    }

    public async Task FocusTerminalAsync()
    {
        await InitializeAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            Focusable = true;
            Browser.Focus();
            Keyboard.Focus(Browser);
        }, DispatcherPriority.Input);
        await Browser.CoreWebView2.ExecuteScriptAsync("window.focus(); window.crtFocus()");
    }

    public async Task ShowSearchAsync()
    {
        await InitializeAsync();
        SearchBar.Visibility = Visibility.Visible;
        await Dispatcher.InvokeAsync(() =>
        {
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public async Task HideSearchAsync()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchResultText.Text = string.Empty;
        await InitializeAsync();
        await Browser.CoreWebView2.ExecuteScriptAsync("window.crtClearSearch()");
        await FocusTerminalAsync();
    }

    public async Task ClearAsync()
    {
        await InitializeAsync();
        await Browser.CoreWebView2.ExecuteScriptAsync("window.crtClear()");
    }

    public async Task SelectAllAsync()
    {
        await InitializeAsync();
        await Browser.CoreWebView2.ExecuteScriptAsync("window.crtSelectAll()");
    }

    public async Task CopySelectionAsync()
    {
        await InitializeAsync();
        await Browser.CoreWebView2.ExecuteScriptAsync("window.crtCopy()");
    }

    public async Task PasteClipboardAsync()
    {
        await InitializeAsync();
        await PasteClipboardCoreAsync();
    }

    /// <summary>
    /// 把 WebView2 控制器重挂到 Browser 控件当前的 HwndHost 句柄。
    /// 跨窗口转移标签后必须调用：WPF 可能在移动控件时重建 HwndHost 句柄，
    /// 而控制器仍指向旧句柄（原窗口），原窗口关闭后渲染断链（标签变空白）。
    /// 注意父窗口必须是控件自身的 HWND（相对控件定位），不能是顶层窗口。
    /// </summary>
    public void RebindToWindow(Window window)
    {
        if (Browser.CoreWebView2 is null || _disposed)
        {
            return;
        }
        try
        {
            var method = typeof(Microsoft.Web.WebView2.Wpf.WebView2).GetMethod(
                "ReparentController",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [typeof(IntPtr), typeof(bool)], null);
            if (method is null)
            {
                return;
            }
            var hwnd = Browser is System.Windows.Interop.IWin32Window win32
                ? win32.Handle
                : IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            method.Invoke(Browser, [hwnd, true]);
            RebindParentWindowLifetime(window);
            // 触发布局，让 OnWindowPositionChanged 按新父窗口重算渲染边界
            Browser.InvalidateMeasure();
            Browser.UpdateLayout();
        }
        catch (Exception exception)
        {
            // 重挂失败不影响会话数据；症状是原窗口关闭后该标签渲染断链
            System.Diagnostics.Debug.WriteLine($"RebindToWindow 失败: {exception}");
        }
    }

    /// <summary>
    /// WebView2 初始化时订阅了创建它的窗口的 Closed 事件（ParentWindow_Closed），
    /// 原窗口关闭会直接终止控制器——这是跨窗口转移后标签变空白的根因。
    /// 这里把 _parentWindow 指向新窗口，并把 Closed 订阅从旧窗口迁移到新窗口。
    /// </summary>
    private void RebindParentWindowLifetime(Window newWindow)
    {
        var baseField = typeof(Microsoft.Web.WebView2.Wpf.WebView2).GetField(
            "m_webview2Base", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException("未找到 m_webview2Base 字段");
        var webView2Base = baseField.GetValue(Browser)
            ?? throw new InvalidOperationException("WebView2Base 尚未创建");

        var parentWindowField = webView2Base.GetType().GetField(
            "_parentWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException("未找到 _parentWindow 字段");
        var handlerMethod = webView2Base.GetType().GetMethod(
            "ParentWindow_Closed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException("未找到 ParentWindow_Closed 方法");

        var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), webView2Base, handlerMethod);
        Window? oldWindow = parentWindowField.GetValue(webView2Base) as Window;
        if (oldWindow is not null)
        {
            oldWindow.Closed -= handler;
        }
        parentWindowField.SetValue(webView2Base, newWindow);
        newWindow.Closed += handler;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        Browser.Dispose();
    }

    private async Task SearchAsync(bool previous, bool incremental)
    {
        var query = SearchTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            SearchResultText.Text = string.Empty;
            await Browser.CoreWebView2.ExecuteScriptAsync("window.crtClearSearch()");
            return;
        }

        await InitializeAsync();
        var serializedQuery = JsonSerializer.Serialize(query);
        var caseSensitive = MatchCaseBox.IsChecked == true ? "true" : "false";
        var function = previous ? "crtFindPrevious" : "crtFindNext";
        var result = await Browser.CoreWebView2.ExecuteScriptAsync(
            $"window.{function}({serializedQuery}, {caseSensitive}, {(incremental ? "true" : "false")})");
        if (string.Equals(result, "false", StringComparison.OrdinalIgnoreCase))
        {
            SearchResultText.Text = "无结果";
        }
    }

    private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        await SearchAsync(previous: false, incremental: true);

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await HideSearchAsync();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync(
                previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                incremental: false);
        }
    }

    private async void SearchPrevious_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(previous: true, incremental: false);

    private async void SearchNext_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(previous: false, incremental: false);

    private async void MatchCaseBox_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(previous: false, incremental: true);

    private async void CloseSearch_Click(object sender, RoutedEventArgs e) =>
        await HideSearchAsync();

    private async Task PasteClipboardCoreAsync()
    {
        if (!Clipboard.ContainsText())
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(Clipboard.GetText());
        var base64 = Convert.ToBase64String(bytes);
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.crtPaste('{base64}')");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    _ready.TrySetResult();
                    break;
                case "input":
                    InputReceived?.Invoke(
                        this,
                        new TerminalInputEventArgs(root.GetProperty("data").GetString() ?? string.Empty));
                    break;
                case "resize":
                    TerminalResized?.Invoke(
                        this,
                        new TerminalResizeEventArgs(
                            root.GetProperty("cols").GetInt32(),
                            root.GetProperty("rows").GetInt32()));
                    break;
                case "copy":
                    var selected = root.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(selected))
                    {
                        Clipboard.SetText(selected);
                    }
                    break;
                case "paste":
                    await PasteClipboardCoreAsync();
                    break;
                case "find":
                    await ShowSearchAsync();
                    break;
                case "searchResults":
                    var count = root.GetProperty("count").GetInt32();
                    var index = root.GetProperty("index").GetInt32();
                    SearchResultText.Text = count == 0
                        ? "无结果"
                        : index >= 0 ? $"{index + 1}/{count}" : $"{count}+";
                    break;
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
    }
}
