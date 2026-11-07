namespace Eteris.Browser.Tabs;

// Uma ação do Éteris que se pode desfazer: Label diz o que foi feito («Feito: «Wikipédia»»), Undo repõe-no.
public sealed record UndoEntry(string Label, Action Undo);

// Pilha de anulação das ações destrutivas do Éteris (fechar abas, Feito, Adiar, Arquivo, remover favoritos…), por
// janela e só em memória. Ctrl+Z (fora da página e dos campos de texto), o «Anular» do aviso e a paleta tiram daqui.
// Uma entrada sai quando é anulada — pelo botão do aviso ou pelo Ctrl+Z — e nunca corre duas vezes.
public sealed class UndoStack
{
    public const int Max = 25;

    private readonly List<UndoEntry> _entries = [];

    public int Count => _entries.Count;

    public UndoEntry? Peek => _entries.Count > 0 ? _entries[^1] : null;

    public UndoEntry Push(string label, Action undo)
    {
        var entry = new UndoEntry(label, undo);
        _entries.Add(entry);
        if (_entries.Count > Max) _entries.RemoveAt(0);
        return entry;
    }

    // Anula a mais recente; null quando não há nada para anular.
    public UndoEntry? UndoLast()
    {
        if (_entries.Count == 0) return null;
        var entry = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        entry.Undo();
        return entry;
    }

    // O «Anular» de um aviso: só corre se a entrada ainda lá estiver (não foi anulada pelo Ctrl+Z entretanto).
    public bool Undo(UndoEntry entry)
    {
        if (!_entries.Remove(entry)) return false;
        entry.Undo();
        return true;
    }

    public void Clear() => _entries.Clear();
}

// Quanto tempo fica um aviso: o simples desvanece depressa; com botões fica o tempo de os ler e carregar.
public static class ToastPolicy
{
    public const int MaxActions = 2;

    public static TimeSpan Hold(int actions) => actions > 0 ? TimeSpan.FromSeconds(6) : TimeSpan.FromMilliseconds(1900);

    // «Feito: «Um título muito comprido…»» — o nome da aba no rótulo da anulação, cortado para caber na paleta.
    public static string Quote(string title, int max = 48)
        => "«" + (title.Length <= max ? title : title[..(max - 1)].TrimEnd() + "…") + "»";
}
