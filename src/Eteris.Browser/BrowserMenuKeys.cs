using System.Globalization;
using System.Text;
using System.Windows.Input;

namespace Eteris.Browser;

// O que uma tecla faz com o menu do Éteris aberto. Decisão pura (sem WPF), usada na thread da janela e na do CEF.
public enum BrowserMenuKey
{
    // Tecla que o menu não usa: na janela segue o caminho normal do WPF; vinda da página, é consumida (Ignore).
    None,
    Close,
    // ↑ e ↓: de linha em linha (a linha do zoom conta uma vez).
    Previous,
    Next,
    First,
    Last,
    // ← e →: só dentro de um grupo (a linha do zoom: −, 100%, +, ecrã inteiro); numa linha simples não fazem nada.
    PreviousInGroup,
    NextInGroup,
    // Letra sem modificadores: salta para a próxima linha cujo nome começa por ela.
    Initial,
    Activate,
    NextField,
    PreviousField,
    // Atalho do Éteris: fecha o menu e corre.
    Shortcut,
    // Vinda da página, consumida sem fazer nada (PgUp/PgDn, Espaço com modificadores, Alt/Ctrl/Shift/Win sozinhos…):
    // nunca chega à página.
    Ignore,
}

public static class BrowserMenuKeys
{
    // Navegação no menu, como nos menus do Chrome, do Firefox e do Brave: Esc, Alt+F e Alt+E fecham; ↑ e ↓ movem de
    // linha em linha, Home e End vão ao início e ao fim (com volta); ← e → só andam dentro da linha do zoom; uma letra
    // salta para a linha com essa inicial; Enter e Espaço ativam; Tab e Shift+Tab percorrem.
    public static BrowserMenuKey Resolve(Key key, bool ctrl, bool shift, bool alt)
    {
        if (key == Key.Escape && !ctrl && !alt) return BrowserMenuKey.Close;
        if (key is Key.F or Key.E && alt && !ctrl && !shift) return BrowserMenuKey.Close;
        if (ctrl || alt) return BrowserMenuKey.None;
        if (key == Key.Tab) return shift ? BrowserMenuKey.PreviousField : BrowserMenuKey.NextField;
        if (shift) return BrowserMenuKey.None;
        return key switch
        {
            Key.Up => BrowserMenuKey.Previous,
            Key.Down => BrowserMenuKey.Next,
            Key.Left => BrowserMenuKey.PreviousInGroup,
            Key.Right => BrowserMenuKey.NextInGroup,
            Key.Home => BrowserMenuKey.First,
            Key.End => BrowserMenuKey.Last,
            Key.Enter or Key.Space => BrowserMenuKey.Activate,
            >= Key.A and <= Key.Z => BrowserMenuKey.Initial,
            _ => BrowserMenuKey.None,
        };
    }

    // Tecla premida com o foco Win32 ainda na página (thread do CEF) e o menu aberto: o que o menu usa é feito pelo menu,
    // um atalho do Éteris fecha o menu e corre, e tudo o resto é consumido — as setas, o Esc, o Enter, o Espaço, as
    // letras e até os modificadores sozinhos (Alt, Ctrl, Shift, Win) nunca chegam à página enquanto o menu está aberto,
    // como nos menus do Chrome, do Firefox e do Brave. O «Alt sozinho» vê o keydown na mesma (ShortcutFromPage), e o
    // keyup que o Chromium descarta é compensado quando o menu fecha (AltTap.Reset).
    public static BrowserMenuKey FromPage(Key key, bool ctrl, bool shift, bool alt, bool isShortcut)
    {
        if (Resolve(key, ctrl, shift, alt) is var menu and not BrowserMenuKey.None) return menu;
        return isShortcut ? BrowserMenuKey.Shortcut : BrowserMenuKey.Ignore;
    }

    // Próxima linha (índice em linhas ativas) para Previous/Next/First/Last, com volta ao início e ao fim.
    // current = -1 quando nenhuma linha tem o foco.
    public static int Move(BrowserMenuKey move, int current, int count)
    {
        var rows = new int[Math.Max(count, 0)];
        for (var i = 0; i < rows.Length; i++) rows[i] = i;
        return Move(move, current, rows);
    }

    // O mesmo com grupos: rows[i] é a linha do controlo i (os controlos da mesma linha, como os do zoom, são seguidos e
    // partilham o número). ↑ e ↓ saltam de linha em linha e entram num grupo pelo primeiro controlo; ← e → só se movem
    // dentro do grupo (sem volta) e numa linha simples não fazem nada. current = -1 quando nada tem o foco.
    public static int Move(BrowserMenuKey move, int current, IReadOnlyList<int> rows)
    {
        var count = rows.Count;
        if (count <= 0) return -1;
        int StartOfRow(int index)
        {
            while (index > 0 && rows[index - 1] == rows[index]) index--;
            return index;
        }
        int EndOfRow(int index)
        {
            while (index < count - 1 && rows[index + 1] == rows[index]) index++;
            return index;
        }
        if (current >= count) current = -1;
        if (current < 0)
            return move switch
            {
                BrowserMenuKey.First or BrowserMenuKey.Next => 0,
                BrowserMenuKey.Last or BrowserMenuKey.Previous => StartOfRow(count - 1),
                _ => -1,
            };
        var start = StartOfRow(current);
        var end = EndOfRow(current);
        return move switch
        {
            BrowserMenuKey.First => 0,
            BrowserMenuKey.Last => StartOfRow(count - 1),
            BrowserMenuKey.Previous => StartOfRow(start > 0 ? start - 1 : count - 1),
            BrowserMenuKey.Next => end < count - 1 ? end + 1 : 0,
            BrowserMenuKey.PreviousInGroup => current > start ? current - 1 : current,
            BrowserMenuKey.NextInGroup => current < end ? current + 1 : current,
            _ => current,
        };
    }

    // Navegação pela inicial, como nos menus do Chrome e do Brave: a letra salta para a próxima linha (depois da que tem
    // o foco, com volta) cujo nome começa por ela, sem olhar a maiúsculas nem a acentos. labels[i] null: o controlo não
    // conta (os do zoom). -1 se nenhuma linha começar pela letra.
    public static int NextByInitial(Key key, int current, IReadOnlyList<string?> labels)
    {
        if (key is not (>= Key.A and <= Key.Z) || labels.Count == 0) return -1;
        var letter = (char)('a' + (key - Key.A));
        var from = current < 0 || current >= labels.Count ? -1 : current;
        for (var step = 1; step <= labels.Count; step++)
        {
            var index = (from + step) % labels.Count;
            if (InitialOf(labels[index]) == letter) return index;
        }
        return -1;
    }

    private static char? InitialOf(string? label)
    {
        var text = label?.TrimStart();
        if (string.IsNullOrEmpty(text)) return null;
        var bare = text[..1].Normalize(NormalizationForm.FormD)
            .FirstOrDefault(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return bare == default ? null : char.ToLowerInvariant(bare);
    }
}
