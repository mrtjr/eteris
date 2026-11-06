using System.Windows;
using Microsoft.Win32;

namespace Eteris.Browser.Theme;

// Troca a paleta da aplicação (a primeira entrada das MergedDictionaries) entre escuro e claro, em tempo real.
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action<AppTheme>? Changed;

    public static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception) { return false; }
    }

    public static void Apply(string choice)
    {
        var theme = ThemeResolver.Resolve(choice, SystemUsesLight());
        if (theme == Current && Application.Current.Resources.MergedDictionaries.Count > 0) return;
        Current = theme;
        var palette = new ResourceDictionary { Source = new Uri(ThemeResolver.PalettePath(theme), UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = palette;
        else merged.Insert(0, palette);
        Changed?.Invoke(theme);
    }
}
