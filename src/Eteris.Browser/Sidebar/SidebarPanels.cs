namespace Eteris.Browser.Sidebar;

// Barra lateral unificada (Ctrl+Shift+B): uma coluna ao lado da página — à esquerda ou à direita, redimensionável
// e lembrada — com separadores: Notas, Para ler, Favoritos, Histórico e os sites fixados. Aqui vive só a lógica
// (que painéis há, larguras, lado, margens da página); a janela aplica (MainWindow.Sidebar.cs).
public enum SidebarSide
{
    Right,
    Left,
}

// Um separador da barra: os embutidos têm só a chave; um site fixado traz o endereço (chave «web:<endereço>»).
public sealed record SidebarPanel(string Key, string Title, string Glyph, string? Url = null)
{
    public bool IsWeb => Url is not null;
}

public static class SidebarPanels
{
    public const string Notes = "notes";
    public const string Reading = "reading";
    public const string Bookmarks = "bookmarks";
    public const string History = "history";
    public const string WebPrefix = "web:";

    public const double MinWidth = 280;
    public const double MaxWidth = 720;
    public const double DefaultWidth = 360;

    // Espaço entre a página e a barra lateral (a pega de redimensionar vive nele) e a coluna dos separadores.
    public const double Gap = 6;
    public const double RailWidth = 44;

    public static readonly IReadOnlyList<SidebarPanel> BuiltIn =
    [
        new(Notes, "Notas", ""),
        new(Reading, "Para ler", ""),
        new(Bookmarks, "Favoritos", ""),
        new(History, "Histórico", ""),
    ];

    // Na janela privada ficam de fora os painéis que guardam ou mostram o que fica neste computador (as notas e o
    // histórico, como o Ctrl+H, que também não existe lá); favoritos e lista de leitura continuam à mão.
    public static IReadOnlyList<SidebarPanel> Available(bool isPrivate)
        => isPrivate ? BuiltIn.Where(p => p.Key is Reading or Bookmarks).ToList() : BuiltIn;

    public static bool IsWebKey(string? key)
        => key is not null && key.StartsWith(WebPrefix, StringComparison.Ordinal) && IsWebUrl(key[WebPrefix.Length..]);

    public static bool IsWebUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Chave aceite nas definições: um painel embutido ou um site fixado com endereço http(s).
    public static bool IsKnownKey(string? key) => BuiltIn.Any(p => p.Key == key) || IsWebKey(key);

    public static string WebKey(string url) => WebPrefix + url;

    // Largura dentro dos limites: nunca mais de metade da janela (a página tem de continuar legível).
    public static double ClampWidth(double wanted, double windowWidth)
    {
        if (double.IsNaN(wanted) || wanted <= 0) wanted = DefaultWidth;
        var max = Math.Max(MinWidth, Math.Min(MaxWidth, Math.Floor(windowWidth / 2)));
        return Math.Clamp(wanted, MinWidth, max);
    }

    // Largura nova ao arrastar a pega: à direita, puxar para a esquerda alarga; à esquerda, o contrário.
    public static double Resize(double current, double horizontalChange, SidebarSide side, double windowWidth)
        => ClampWidth(side == SidebarSide.Right ? current - horizontalChange : current + horizontalChange, windowWidth);

    public static SidebarSide ParseSide(string? value) => value == "left" ? SidebarSide.Left : SidebarSide.Right;

    public static string SideName(SidebarSide side) => side == SidebarSide.Left ? "left" : "right";

    // O painel a mostrar: o pedido, se existir; senão o primeiro disponível (null quando não há nenhum).
    public static SidebarPanel? Resolve(string? wanted, IReadOnlyList<SidebarPanel> available)
        => available.FirstOrDefault(p => p.Key == wanted) ?? available.FirstOrDefault();

    // Margens da moldura da página. A barra de abas verticais (à esquerda) já desconta a folga da página; a barra
    // lateral desconta a sua largura menos essa mesma folga, do lado em que está. (Left, Right) em pixels.
    public static (double Left, double Right) PageMargins(double sideTabsWidth, bool open, SidebarSide side, double width)
    {
        var left = sideTabsWidth > 0 ? sideTabsWidth - Gap : 0;
        if (!open) return (left, 0);
        return side == SidebarSide.Left ? (left + width - Gap, 0) : (left, width - Gap);
    }
}
