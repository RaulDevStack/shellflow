namespace ShellFlow.Core.Bridge;

/// <summary>xterm.js ITheme colors. Property names match xterm.js after camelCase serialization.</summary>
public sealed record TerminalTheme
{
    public required string Background { get; init; }
    public required string Foreground { get; init; }
    public required string Cursor { get; init; }
    public required string CursorAccent { get; init; }
    public required string SelectionBackground { get; init; }
    public required string SelectionInactiveBackground { get; init; }
    public required string ScrollbarSliderBackground { get; init; }
    public required string ScrollbarSliderHoverBackground { get; init; }
    public required string ScrollbarSliderActiveBackground { get; init; }
    public required string Black { get; init; }
    public required string Red { get; init; }
    public required string Green { get; init; }
    public required string Yellow { get; init; }
    public required string Blue { get; init; }
    public required string Magenta { get; init; }
    public required string Cyan { get; init; }
    public required string White { get; init; }
    public required string BrightBlack { get; init; }
    public required string BrightRed { get; init; }
    public required string BrightGreen { get; init; }
    public required string BrightYellow { get; init; }
    public required string BrightBlue { get; init; }
    public required string BrightMagenta { get; init; }
    public required string BrightCyan { get; init; }
    public required string BrightWhite { get; init; }
}

/// <summary>ShellFlow's built-in ANSI palettes, tuned to the app's dark and light surfaces.</summary>
public static class TerminalThemes
{
    /// <summary>"Flow Night": soft pastel ANSI colors on #111318. Normal colors (except black) reach ≥ 4.9:1 against the background.</summary>
    public static TerminalTheme Dark(string cursor) => new()
    {
        Background = "#111318",
        Foreground = "#E4E6EE",
        Cursor = cursor,
        CursorAccent = "#111318",
        SelectionBackground = "#A6B7FF40",
        SelectionInactiveBackground = "#A6B7FF24",
        ScrollbarSliderBackground = "#F0F0F51A",
        ScrollbarSliderHoverBackground = "#F0F0F530",
        ScrollbarSliderActiveBackground = "#F0F0F545",
        Black = "#2A2E39",
        Red = "#F2828F",
        Green = "#A3DCC5",
        Yellow = "#EBD39A",
        Blue = "#A6B7FF",
        Magenta = "#D3B1F2",
        Cyan = "#91D8F7",
        White = "#D2D5DF",
        // Bright variants are more saturated (not just lighter) so they stay distinct from the foreground.
        BrightBlack = "#7D8496",
        BrightRed = "#FF97A3",
        BrightGreen = "#8CEDBE",
        BrightYellow = "#FFE08A",
        BrightBlue = "#B4C3FF",
        BrightMagenta = "#E3B5FF",
        BrightCyan = "#8FE3FF",
        BrightWhite = "#FFFFFF",
    };

    /// <summary>"Flow Day": deeper ink colors that stay readable on a light background.</summary>
    public static TerminalTheme Light(string cursor) => new()
    {
        Background = "#FBFBFD",
        Foreground = "#2B2F3A",
        Cursor = cursor,
        CursorAccent = "#FBFBFD",
        SelectionBackground = "#4A5FC133",
        SelectionInactiveBackground = "#4A5FC11F",
        ScrollbarSliderBackground = "#1A1D2414",
        ScrollbarSliderHoverBackground = "#1A1D2426",
        ScrollbarSliderActiveBackground = "#1A1D2438",
        Black = "#2B2F3A",
        Red = "#C23B4B",
        Green = "#257A5A",
        Yellow = "#8A6400",
        Blue = "#3D52B8",
        Magenta = "#8A4AB8",
        Cyan = "#1A7299",
        White = "#5E6475",
        BrightBlack = "#6B7182",
        BrightRed = "#D9505F",
        BrightGreen = "#2F8C68",
        BrightYellow = "#9E7408",
        BrightBlue = "#5266CC",
        BrightMagenta = "#9D5DCB",
        BrightCyan = "#2385B0",
        BrightWhite = "#1A1D24",
    };
}
