namespace CrtSharp.Models;

public enum TerminalColorTheme
{
    Dark,
    Light,
    SolarizedDark,
    Monokai
}

public enum TerminalCursorStyle
{
    Block,
    Underline,
    Bar
}

public sealed record TerminalAppearanceSettings(
    string FontFamily,
    int FontSize,
    TerminalColorTheme Theme,
    TerminalCursorStyle CursorStyle,
    bool CursorBlink,
    string PreviewText);
