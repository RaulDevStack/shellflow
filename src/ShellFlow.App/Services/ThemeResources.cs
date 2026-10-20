using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ShellFlow.App.Services;

/// <summary>Resolves themed token brushes from code for a specific theme (Application.Resources only knows the app theme).</summary>
public static class ThemeResources
{
    public static Brush Brush(string key, ElementTheme theme)
    {
        var themeKey = theme == ElementTheme.Light ? "Light" : "Default";
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary resources &&
                resources.TryGetValue(key, out var value) &&
                value is Brush brush)
            {
                return brush;
            }
        }

        return (Brush)Application.Current.Resources[key];
    }
}
