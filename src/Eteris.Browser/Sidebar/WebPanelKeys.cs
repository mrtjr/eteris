using System.Windows.Input;

namespace Eteris.Browser.Sidebar;

// O que um atalho faz quando o foco está num site fixado da barra lateral.
public enum WebPanelKey
{
    Reload,
    HardReload,
    Find,
    Print,
    DevTools,
}

// Os atalhos «da página» (recarregar, procurar, imprimir, ferramentas de programador) agem sobre o motor que tem o
// foco: com o foco no painel é o site fixado que recarrega, não a aba por trás. Os restantes atalhos do Éteris
// (abas, janelas, barra lateral…) continuam globais. Ctrl+Alt nunca é atalho (é o AltGr).
public static class WebPanelKeys
{
    public static WebPanelKey? Resolve(Key key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && alt) return null;
        // As mesmas teclas de MainWindow.Shortcut, pela mesma ordem.
        if ((ctrl && key == Key.F) || key == Key.F3) return WebPanelKey.Find;
        if (key == Key.F12 || (ctrl && shift && key == Key.I)) return WebPanelKey.DevTools;
        if (ctrl && key == Key.P) return WebPanelKey.Print;
        if ((ctrl && shift && key == Key.R) || (ctrl && key == Key.F5)) return WebPanelKey.HardReload;
        if ((ctrl && key == Key.R) || key == Key.F5) return WebPanelKey.Reload;
        return null;
    }
}
