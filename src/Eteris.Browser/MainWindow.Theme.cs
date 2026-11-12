using System.Windows;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Theme;

namespace Eteris.Browser;

// Tema claro/escuro desta janela. As janelas privadas ficam sempre com a paleta escura (e o véu violeta).
public partial class MainWindow
{
    private static readonly Color LightChrome = Color.FromRgb(0xF6, 0xF7, 0xFB);

    private bool UsesLight => !IsPrivate && ThemeManager.Current == AppTheme.Light;

    // Cor da moldura da janela (COLORREF, BGR).
    private uint BorderColor => IsPrivate ? 0x00ED3A7Cu : UsesLight ? 0x00E4D9D5u : 0x0042302Bu;

    private void WatchTheme()
    {
        if (IsPrivate)
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme/Dark.xaml", UriKind.Relative) });
        void OnChanged(AppTheme _) => Dispatcher.InvokeAsync(ApplyWindowTheme);
        ThemeManager.Changed += OnChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnChanged;
        // Cor do Éteris: a barra aurora desta janela e as páginas internas abertas mudam ao vivo, sem recarregar.
        void OnAccent() => Dispatcher.InvokeAsync(ApplyAccent);
        AccentManager.Changed += OnAccent;
        Closed += (_, _) => AccentManager.Changed -= OnAccent;
        ApplyAccent();
    }

    private void ApplyAccent()
    {
        AccentManager.Retint(Resources["AuroraBrush"] as LinearGradientBrush, AccentManager.Stops);
        var script = Browser.InternalPages.AccentScript(AccentManager.Stops);
        foreach (var (tab, browser) in _browsers)
            if (tab.Address.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase) && !browser.IsDisposed && browser.IsBrowserInitialized)
                Browser.PageScript.Run(browser, script);
    }

    private void ApplyWindowTheme()
    {
        WindowEffects.Apply(this, BorderColor, darkMode: !UsesLight);
        ApplyTint();
        ResetShortcutsForTheme();
        ResetObservatoryForTheme();
        // As páginas internas abertas são geradas com o tema: recarregam para o acompanhar.
        if (IsPrivate) return;
        foreach (var (tab, browser) in _browsers)
            if (tab.Address.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase) && IsReady(browser)) browser.Reload();
    }
}
