using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ShellFlow.Core.Bridge;
using ShellFlow.Core.Settings;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ShellFlow.App.Services;

/// <summary>Applies theme, accent and motion preferences to the native UI and produces the renderer palette.</summary>
public sealed class ThemeService
{
    private static readonly Dictionary<AccentColor, (Color Dark, Color Light)> Accents = new()
    {
        [AccentColor.Periwinkle] = (Rgb(0xA6, 0xB7, 0xFF), Rgb(0x4A, 0x5F, 0xC1)),
        [AccentColor.Sky] = (Rgb(0x91, 0xD8, 0xF7), Rgb(0x1A, 0x72, 0x99)),
        [AccentColor.Mint] = (Rgb(0xA3, 0xDC, 0xC5), Rgb(0x25, 0x7A, 0x5A)),
        [AccentColor.Peach] = (Rgb(0xE8, 0xBF, 0xA6), Rgb(0xA3, 0x5A, 0x2E)),
    };

    private readonly UISettings _uiSettings = new();
    private FrameworkElement? _root;
    private AppWindow? _appWindow;
    private AppearanceSettings _appearance = new();

    public ThemeService()
    {
        _uiSettings.AnimationsEnabledChanged += (_, _) => _root?.DispatcherQueue.TryEnqueue(() => MotionChanged?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? MotionChanged;

    public event EventHandler? ThemeChanged;

    public ElementTheme ActualTheme => _root?.ActualTheme ?? ElementTheme.Dark;

    /// <summary>True when nonessential motion should run, honoring the setting and Windows' "Animation effects".</summary>
    public bool AnimationsEnabled => _appearance.Animations switch
    {
        AnimationPreference.Full => true,
        AnimationPreference.Off => false,
        _ => _uiSettings.AnimationsEnabled,
    };

    /// <summary>True when the Hyprland-style motion set (pop-in, gliding layout, sliding tabs) should run.</summary>
    public bool FluidMotion => AnimationsEnabled && _appearance.MotionStyle == MotionStyle.Fluid;

    /// <summary>Second stop of the active-pane border gradient: the next accent around the palette.</summary>
    public Color SecondaryAccentColor
    {
        get
        {
            var next = _appearance.Accent switch
            {
                AccentColor.Periwinkle => AccentColor.Sky,
                AccentColor.Sky => AccentColor.Mint,
                AccentColor.Mint => AccentColor.Sky,
                _ => AccentColor.Periwinkle,
            };
            return ActualTheme == ElementTheme.Light ? Accents[next].Light : Accents[next].Dark;
        }
    }

    public void Attach(FrameworkElement root, AppWindow appWindow)
    {
        _root = root;
        _appWindow = appWindow;
        root.ActualThemeChanged += (_, _) =>
        {
            ApplyAccent();
            ApplyCaptionButtons();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Apply(AppearanceSettings appearance)
    {
        _appearance = appearance;
        if (_root is null)
        {
            return;
        }

        var requested = appearance.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.System => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };

        var changed = _root.RequestedTheme != requested;
        _root.RequestedTheme = requested;
        ApplyAccent();
        ApplyCaptionButtons();
        MotionChanged?.Invoke(this, EventArgs.Empty);
        if (!changed)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Color AccentColorValue => ActualTheme == ElementTheme.Light ? Accents[_appearance.Accent].Light : Accents[_appearance.Accent].Dark;

    public TerminalTheme TerminalTheme
    {
        get
        {
            var cursor = ToHex(AccentColorValue);
            return ActualTheme == ElementTheme.Light ? TerminalThemes.Light(cursor) : TerminalThemes.Dark(cursor);
        }
    }

    public Color TerminalBackground => ActualTheme == ElementTheme.Light ? Rgb(0xFB, 0xFB, 0xFD) : Rgb(0x11, 0x13, 0x18);

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color? ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return null;
        }

        return Rgb(r, g, b);
    }

    private void ApplyAccent()
    {
        var resources = Application.Current.Resources;
        foreach (var (key, isLight) in new[] { ("Default", false), ("Light", true) })
        {
            if (!resources.MergedDictionaries.Any())
            {
                return;
            }

            var accent = isLight ? Accents[_appearance.Accent].Light : Accents[_appearance.Accent].Dark;
            foreach (var dictionary in resources.MergedDictionaries)
            {
                if (dictionary.ThemeDictionaries.TryGetValue(key, out var themed) && themed is ResourceDictionary theme && theme.ContainsKey("SfAccentBrush"))
                {
                    // Mutating the shared brush instances updates every element that uses them immediately.
                    SetBrush(theme, "SfAccentBrush", accent);
                    SetBrush(theme, "SfAccentSubtleBrush", WithAlpha(accent, isLight ? (byte)0x1F : (byte)0x26));
                    SetBrush(theme, "SfAccentBorderBrush", WithAlpha(accent, 0x99));
                    SetBrush(theme, "AccentFillColorDefaultBrush", accent);
                    SetBrush(theme, "AccentFillColorSecondaryBrush", WithAlpha(accent, 0xE6));
                    SetBrush(theme, "AccentFillColorTertiaryBrush", WithAlpha(accent, 0xCC));
                }
            }
        }
    }

    private void ApplyCaptionButtons()
    {
        if (_appWindow is null)
        {
            return;
        }

        var light = ActualTheme == ElementTheme.Light;
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = light ? Rgb(0x1A, 0x1D, 0x24) : Rgb(0xF0, 0xF0, 0xF5);
        titleBar.ButtonInactiveForegroundColor = light ? Rgb(0x8A, 0x90, 0xA0) : Rgb(0x6E, 0x74, 0x84);
        titleBar.ButtonHoverBackgroundColor = light ? Color.FromArgb(0x14, 0x1A, 0x1D, 0x24) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedBackgroundColor = light ? Color.FromArgb(0x0C, 0x1A, 0x1D, 0x24) : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
    }

    private static void SetBrush(ResourceDictionary dictionary, string key, Color color)
    {
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
}
