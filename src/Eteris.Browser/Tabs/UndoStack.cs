namespace Eteris.Browser.Tabs;

// Uma ação do Éteris que se pode desfazer: Label diz o que foi feito («Feito: «Wikipédia»»), Undo repõe-no e diz se
// havia ainda alguma coisa a repor (false: a aba já tinha sido reaberta por outro caminho — Ctrl+Shift+T, o Radar).
// Addresses: os endereços que a entrada traz de volta (para «Triturar» e «Esquecer» os poderem tirar da pilha).
public sealed record UndoEntry(string Label, Func<bool> Undo)
{
    public IReadOnlyList<string> Addresses { get; init; } = [];
}

// Pilha de anulação das ações destrutivas do Éteris (fechar abas, Feito, Adiar, Arquivo, remover favoritos…), por
// janela e só em memória. Ctrl+Z (fora da página e dos campos de texto), o «Anular» do aviso e a paleta tiram daqui.
// Uma entrada sai quando é anulada — pelo botão do aviso ou pelo Ctrl+Z — e nunca corre duas vezes.
public sealed class UndoStack
{
    public const int Max = 25;

    private readonly List<UndoEntry> _entries = [];

    public int Count => _entries.Count;

    public UndoEntry? Peek => _entries.Count > 0 ? _entries[^1] : null;

    public UndoEntry Push(string label, Func<bool> undo, IEnumerable<string>? addresses = null)
    {
        var entry = new UndoEntry(label, undo) { Addresses = addresses?.ToList() ?? [] };
        _entries.Add(entry);
        if (_entries.Count > Max) _entries.RemoveAt(0);
        return entry;
    }

    public UndoEntry Push(string label, Action undo, IEnumerable<string>? addresses = null)
        => Push(label, () =>
        {
            undo();
            return true;
        }, addresses);

    // Anula a mais recente que ainda tenha efeito (as que já não têm nada para repor saem sem gastar o Ctrl+Z);
    // null quando não há nada para anular.
    public UndoEntry? UndoLast()
    {
        while (_entries.Count > 0)
        {
            var entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            if (entry.Undo()) return entry;
        }
        return null;
    }

    // O «Anular» de um aviso: só corre se a entrada ainda lá estiver (não foi anulada pelo Ctrl+Z entretanto) e diz
    // se houve alguma coisa a repor.
    public bool Undo(UndoEntry entry) => _entries.Remove(entry) && entry.Undo();

    // «Triturar» e «Esquecer» um site: as entradas que o reabririam saem (e o título deixa de aparecer na paleta).
    public int RemoveWhere(Func<UndoEntry, bool> match) => _entries.RemoveAll(e => match(e));

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
