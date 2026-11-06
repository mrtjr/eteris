namespace Eteris.Browser.Theme;

public enum AppTheme { Dark, Light }

public static class ThemeResolver
{
    public static AppTheme Resolve(string choice, bool systemUsesLight) => choice switch
    {
        "light" => AppTheme.Light,
        "system" => systemUsesLight ? AppTheme.Light : AppTheme.Dark,
        _ => AppTheme.Dark,
    };

    public static string PalettePath(AppTheme theme) => theme == AppTheme.Light ? "Theme/Light.xaml" : "Theme/Dark.xaml";
}
