namespace Eteris.Browser.Tabs;

// Identidade dada pelo utilizador a uma aba: nome próprio, emoji e cor do ícone. Sobrevive a navegações dentro do
// mesmo site (e à primeira navegação a partir da nova aba) e desaparece quando a aba muda de site — o nome era
// daquela página, não da aba. Lógica sem interface; o BrowserTab aplica-a e o MainWindow.TabIdentity.cs desenha-a.
public static class TabIdentity
{
    public const int MaxNameLength = 60;

    // Emojis do seletor: um por tipo de tarefa, todos de uma só forma (sem variantes de pele) para caberem no ícone.
    public static readonly string[] Emojis =
        ["📌", "⭐", "🔥", "📚", "💼", "🛒", "🎵", "🎬", "✈️", "🏠", "💡", "🧪", "📰", "💬", "🎮", "❤️"];

    // Uma linha, sem espaços a mais, até MaxNameLength caracteres; vazio = sem nome próprio.
    public static string? NormalizeName(string? raw)
    {
        if (raw is null) return null;
        var text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength].TrimEnd();
    }

    // Só os emojis do seletor: o ícone da aba tem 16 px e qualquer outro texto ficaria ilegível.
    public static string? NormalizeEmoji(string? raw)
        => raw is not null && Emojis.Contains(raw) ? raw : null;

    public static int? NormalizeColor(int? index)
        => index is { } i && i >= 0 && i < TabGroup.Palette.Length ? i : null;

    // O nome fica ao navegar dentro do mesmo site; a nova aba não conta como site (o nome dado antes de navegar fica).
    public static bool KeepsIdentity(string oldAddress, string newAddress)
        => Browser.NewTabPage.IsNewTab(oldAddress) || BrowserTab.SameHost(oldAddress, newAddress);

    public static string DisplayTitle(string? customName, string title, string host)
        => customName ?? (string.IsNullOrWhiteSpace(title) ? host : title);

    // Texto do ícone personalizado: o emoji, senão a letra do site sobre a cor escolhida.
    public static string IconText(string? emoji, string? monogram) => emoji ?? monogram ?? "É";

    public static bool HasCustomIcon(string? emoji, int? colorIndex) => emoji is not null || colorIndex is not null;
}
