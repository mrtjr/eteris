using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

// Espaço: um ambiente de abas dentro da mesma janela (Pessoal, Trabalho, Estudo…), com nome, cor da paleta dos
// grupos e um emoji do seletor. Só o Espaço à vista tem as abas na faixa (TabsViewModel.Tabs); os outros guardam
// as suas aqui, a dormir, com a aba ativa e o ecrã dividido, para retomarem exatamente onde ficaram ao voltar.
// A lógica de mudar, mover abas e guardar na sessão está no TabsViewModel; este ficheiro é o modelo e as regras.
public sealed partial class Workspace : ObservableObject
{
    public const int MaxNameLength = 30;
    public const string DefaultName = "Geral";

    // Os mesmos emojis do seletor das abas: cabem no chip e já têm glifo garantido.
    public static string[] Icons => TabIdentity.Emojis;

    public Workspace(string? name = null, int colorIndex = 0, string? icon = null)
    {
        _name = NormalizeName(name) ?? DefaultName;
        _colorIndex = NormalizeColor(colorIndex);
        _icon = NormalizeIcon(icon);
    }

    [NotifyPropertyChangedFor(nameof(Monogram))]
    [NotifyPropertyChangedFor(nameof(ChipText))]
    [ObservableProperty]
    private string _name;

    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    [ObservableProperty]
    private int _colorIndex;

    [NotifyPropertyChangedFor(nameof(ChipText))]
    [ObservableProperty]
    private string? _icon;

    // O Espaço à vista (as suas abas estão na faixa).
    [ObservableProperty]
    private bool _isCurrent;

    // Quantas abas tem (as do Espaço à vista contam-se na faixa; as dos outros, na reserva).
    [NotifyPropertyChangedFor(nameof(Summary))]
    [ObservableProperty]
    private int _tabCount;

    public Brush ColorBrush => TabGroup.BrushAt(ColorIndex);

    public string Monogram => MonogramOf(Name);

    // O que o chip mostra: o emoji, senão a inicial do nome.
    public string ChipText => Icon ?? Monogram;

    public string Summary => $"{Name} · {CountText(TabCount)}";

    // Nunca foi mexido: o único Espaço «Geral» sem cor nem ícone próprios não vale a pena ir para a sessão.
    public bool IsPristine => Name == DefaultName && ColorIndex == 0 && Icon is null;

    // ---------- Reserva: o estado enquanto o Espaço não está à vista ----------

    internal List<BrowserTab> StoredTabs { get; } = [];

    internal int StoredActiveIndex { get; set; }

    // Ecrã dividido gravado ao sair do Espaço (índices na reserva); a janela repõe-o ao voltar.
    public Data.SessionSplit? StoredSplit { get; set; }

    public void Rename(string? name, int? colorIndex = null, string? icon = null)
    {
        Name = NormalizeName(name) ?? Name;
        if (colorIndex is { } color) ColorIndex = NormalizeColor(color);
        Icon = NormalizeIcon(icon);
    }

    // ---------- Regras ----------

    // Uma linha, sem espaços a mais, até MaxNameLength caracteres; vazio = sem nome (o chamador dá um).
    public static string? NormalizeName(string? raw)
    {
        if (raw is null) return null;
        var text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength].TrimEnd();
    }

    public static string? NormalizeIcon(string? raw) => raw is not null && Icons.Contains(raw) ? raw : null;

    public static int NormalizeColor(int index) => ((index % TabGroup.Palette.Length) + TabGroup.Palette.Length) % TabGroup.Palette.Length;

    // Nome para o N.º Espaço criado («Espaço 2»); o primeiro chama-se «Geral».
    public static string DefaultNameFor(int ordinal) => ordinal <= 1 ? DefaultName : $"Espaço {ordinal}";

    // A primeira cor da paleta que nenhum Espaço usa; esgotadas, repete pela ordem.
    public static int NextColor(IEnumerable<int> used)
    {
        var taken = used.ToList();
        for (var i = 0; i < TabGroup.Palette.Length; i++)
            if (!taken.Contains(i)) return i;
        return NormalizeColor(taken.Count);
    }

    // A inicial do nome, em maiúscula (letras e dígitos; de resto «E» de Espaço).
    public static string MonogramOf(string name)
    {
        foreach (var rune in name.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) return Rune.ToUpperInvariant(rune).ToString();
        return "E";
    }

    public static string CountText(int count) => count == 1 ? "1 aba" : $"{count} abas";

    // Ctrl+Alt+1…9: o Espaço N (9 = o último, como nas abas).
    public static int IndexForNumber(int number, int count)
        => count == 0 || number < 1 ? -1 : number >= 9 ? count - 1 : Math.Min(number, count) - 1;
}
