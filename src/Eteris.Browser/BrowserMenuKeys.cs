using System.Windows.Input;

namespace Eteris.Browser;

// O que uma tecla faz com o menu do Éteris aberto. Decisão pura (sem WPF), usada na thread da janela e na do CEF.
public enum BrowserMenuKey
{
    // Tecla que o menu não usa: na janela segue o caminho normal do WPF; vinda da página, é consumida (Ignore).
    None,
    Close,
    Previous,
    Next,
    First,
    Last,
    Activate,
    NextField,
    PreviousField,
    // Atalho do Éteris: fecha o menu e corre.
    Shortcut,
    // Vinda da página, consumida sem fazer nada (as letras, PgUp/PgDn, Espaço com modificadores…): nunca chega à página.
    Ignore,
    // Vinda da página e deixada passar: só os modificadores sozinhos (não fazem nada na página, e o «Alt sozinho» precisa deles).
    PassToPage,
}

public static class BrowserMenuKeys
{
    // Navegação no menu, como nos menus do Chrome, do Firefox e do Brave: Esc e Alt+F fecham; setas, Home e End movem
    // (com volta ao início e ao fim); Enter e Espaço ativam; Tab e Shift+Tab percorrem.
    public static BrowserMenuKey Resolve(Key key, bool ctrl, bool shift, bool alt)
    {
        if (key == Key.Escape && !ctrl && !alt) return BrowserMenuKey.Close;
        if (key == Key.F && alt && !ctrl && !shift) return BrowserMenuKey.Close;
        if (ctrl || alt) return BrowserMenuKey.None;
        if (key == Key.Tab) return shift ? BrowserMenuKey.PreviousField : BrowserMenuKey.NextField;
        if (shift) return BrowserMenuKey.None;
        return key switch
        {
            Key.Up or Key.Left => BrowserMenuKey.Previous,
            Key.Down or Key.Right => BrowserMenuKey.Next,
            Key.Home => BrowserMenuKey.First,
            Key.End => BrowserMenuKey.Last,
            Key.Enter or Key.Space => BrowserMenuKey.Activate,
            _ => BrowserMenuKey.None,
        };
    }

    // Tecla premida com o foco Win32 ainda na página (thread do CEF) e o menu aberto: o que o menu usa é feito pelo menu,
    // um atalho do Éteris fecha o menu e corre, e tudo o resto é consumido — as setas, o Esc, o Enter e o Espaço nunca
    // chegam à página enquanto o menu está aberto (nem a rolam, nem param o carregamento).
    public static BrowserMenuKey FromPage(Key key, bool ctrl, bool shift, bool alt, bool isShortcut)
    {
        if (Resolve(key, ctrl, shift, alt) is var menu and not BrowserMenuKey.None) return menu;
        if (isShortcut) return BrowserMenuKey.Shortcut;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System) return BrowserMenuKey.PassToPage;
        return BrowserMenuKey.Ignore;
    }

    // Próxima linha (índice em linhas ativas) para Previous/Next/First/Last, com volta ao início e ao fim.
    // current = -1 quando nenhuma linha tem o foco.
    public static int Move(BrowserMenuKey move, int current, int count)
    {
        if (count <= 0) return -1;
        return move switch
        {
            BrowserMenuKey.First => 0,
            BrowserMenuKey.Last => count - 1,
            BrowserMenuKey.Previous => current <= 0 ? count - 1 : current - 1,
            BrowserMenuKey.Next => current < 0 || current >= count - 1 ? 0 : current + 1,
            _ => current,
        };
    }
}
