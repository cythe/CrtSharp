using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrtSharp.Models;

namespace CrtSharp;

public partial class TerminalSettingsDialog : Window
{
    public TerminalSettingsDialog(TerminalAppearanceSettings settings)
    {
        InitializeComponent();

        var fonts = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!fonts.Contains(settings.FontFamily, StringComparer.CurrentCultureIgnoreCase))
        {
            fonts.Insert(0, settings.FontFamily);
        }
        FontFamilyBox.ItemsSource = fonts;

        var sizes = new List<int> { 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48 };
        if (!sizes.Contains(settings.FontSize))
        {
            sizes.Add(settings.FontSize);
            sizes.Sort();
        }
        FontSizeBox.ItemsSource = sizes;

        ThemeBox.ItemsSource = new[]
        {
            new EnumOption<TerminalColorTheme>("深色", TerminalColorTheme.Dark),
            new EnumOption<TerminalColorTheme>("浅色", TerminalColorTheme.Light),
            new EnumOption<TerminalColorTheme>("Solarized Dark", TerminalColorTheme.SolarizedDark),
            new EnumOption<TerminalColorTheme>("Monokai", TerminalColorTheme.Monokai)
        };
        CursorStyleBox.ItemsSource = new[]
        {
            new EnumOption<TerminalCursorStyle>("块状", TerminalCursorStyle.Block),
            new EnumOption<TerminalCursorStyle>("下划线", TerminalCursorStyle.Underline),
            new EnumOption<TerminalCursorStyle>("竖线", TerminalCursorStyle.Bar)
        };

        PreviewText.Text = settings.PreviewText;
        FontFamilyBox.SelectedItem = fonts.First(name =>
            string.Equals(name, settings.FontFamily, StringComparison.CurrentCultureIgnoreCase));
        FontSizeBox.SelectedItem = settings.FontSize;
        ThemeBox.SelectedValue = settings.Theme;
        CursorStyleBox.SelectedValue = settings.CursorStyle;
        CursorBlinkBox.IsChecked = settings.CursorBlink;
        UpdatePreview();
    }

    public TerminalAppearanceSettings? Result { get; private set; }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null || PreviewBorder is null)
        {
            return;
        }

        if (FontFamilyBox.SelectedItem is string fontName)
        {
            PreviewText.FontFamily = new FontFamily(fontName);
        }
        if (FontSizeBox.SelectedItem is int fontSize)
        {
            PreviewText.FontSize = fontSize;
        }

        var theme = ThemeBox.SelectedValue is TerminalColorTheme value
            ? value
            : TerminalColorTheme.Dark;
        var (background, foreground) = theme switch
        {
            TerminalColorTheme.Light => (Color.FromRgb(245, 245, 245), Color.FromRgb(30, 30, 30)),
            TerminalColorTheme.SolarizedDark => (Color.FromRgb(0, 43, 54), Color.FromRgb(131, 148, 150)),
            TerminalColorTheme.Monokai => (Color.FromRgb(39, 40, 34), Color.FromRgb(248, 248, 242)),
            _ => (Color.FromRgb(12, 12, 12), Color.FromRgb(204, 204, 204))
        };
        PreviewBorder.Background = new SolidColorBrush(background);
        PreviewText.Foreground = new SolidColorBrush(foreground);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (FontFamilyBox.SelectedItem is not string fontFamily ||
            FontSizeBox.SelectedItem is not int fontSize ||
            ThemeBox.SelectedValue is not TerminalColorTheme theme ||
            CursorStyleBox.SelectedValue is not TerminalCursorStyle cursorStyle)
        {
            return;
        }

        Result = new TerminalAppearanceSettings(
            fontFamily,
            Math.Clamp(fontSize, 8, 72),
            theme,
            cursorStyle,
            CursorBlinkBox.IsChecked == true,
            PreviewText.Text);
        DialogResult = true;
    }

    private sealed record EnumOption<T>(string Display, T Value) where T : struct, Enum;
}
