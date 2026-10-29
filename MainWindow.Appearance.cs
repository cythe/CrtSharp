using System.Windows;
using CrtSharp.Models;

namespace CrtSharp;

public partial class MainWindow
{
    private async void TerminalAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is not { } active)
        {
            StatusText.Text = "请先打开或选择一个终端标签";
            return;
        }

        await EditTerminalAppearanceAsync(active);
    }

    private async Task EditTerminalAppearanceAsync(ActiveSession active)
    {
        var dialog = new TerminalSettingsDialog(active.Profile.GetTerminalAppearance())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        ApplyAppearanceToProfile(active.Profile, dialog.Result);
        await active.Terminal.ApplyAppearanceAsync(dialog.Result);
        StatusText.Text = $"{active.Profile.Name} — 终端外观已应用（仅当前标签）";
        await active.Terminal.FocusTerminalAsync();
    }

    private static void ApplyAppearanceToProfile(
        SessionProfile profile,
        TerminalAppearanceSettings settings)
    {
        profile.TerminalFontFamily = settings.FontFamily;
        profile.TerminalFontSize = settings.FontSize;
        profile.TerminalTheme = settings.Theme;
        profile.TerminalCursorStyle = settings.CursorStyle;
        profile.TerminalCursorBlink = settings.CursorBlink;
        profile.TerminalPreviewText = settings.PreviewText;
    }
}
