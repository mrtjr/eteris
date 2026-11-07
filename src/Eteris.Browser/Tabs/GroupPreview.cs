using System.Windows.Media;

namespace Eteris.Browser.Tabs;

// Um mosaico do cartão de pré-visualização de um grupo: uma aba do grupo, com miniatura quando a há.
public sealed record GroupPreviewTile(BrowserTab Tab, string Title, string Host, ImageSource? Thumbnail, bool IsActive);

// Cartão que aparece ao pairar na etiqueta de um grupo (encolhido ou não): miniaturas e títulos das abas do grupo,
// pela ordem da faixa. Lógica sem interface — o MainWindow.Groups.cs só desenha o que sai daqui.
public static class GroupPreview
{
    public const double TileWidth = 150;
    public const double TilePadding = 8;
    public const int MaxColumns = 3;
    public const int MaxTiles = 12;

    // Tempo a pairar antes de o cartão aparecer, e tempo fora da etiqueta/cartão antes de desaparecer.
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(280);

    public static IReadOnlyList<GroupPreviewTile> Tiles(TabGroup group, IReadOnlyList<BrowserTab> tabs, BrowserTab? active)
        => tabs.Where(t => ReferenceEquals(t.Group, group))
            .Take(MaxTiles)
            .Select(t => new GroupPreviewTile(t, string.IsNullOrWhiteSpace(t.Title) ? t.Host : t.Title, t.Host, t.Thumbnail,
                ReferenceEquals(t, active)))
            .ToList();

    // Até três colunas; poucas abas ocupam só as colunas de que precisam.
    public static int Columns(int count) => Math.Clamp(count, 1, MaxColumns);

    public static double CardWidth(int count) => Columns(count) * (TileWidth + TilePadding) + TilePadding;

    // Centrado por baixo da etiqueta, sem sair da área (margem de 8 px de cada lado).
    public static double Left(double pillLeft, double pillWidth, double cardWidth, double areaWidth)
    {
        const double margin = 8;
        var centered = pillLeft + pillWidth / 2 - cardWidth / 2;
        var max = Math.Max(margin, areaWidth - cardWidth - margin);
        return Math.Clamp(centered, margin, max);
    }

    // Linha do cabeçalho: «3 abas · encolhido · 2 a dormir».
    public static string Summary(TabGroup group, IReadOnlyList<BrowserTab> tabs)
    {
        var members = tabs.Where(t => ReferenceEquals(t.Group, group)).ToList();
        var parts = new List<string> { members.Count == 1 ? "1 aba" : $"{members.Count} abas" };
        if (group.IsCollapsed) parts.Add("encolhido");
        var sleeping = members.Count(t => t.IsSleeping);
        if (sleeping > 0) parts.Add(sleeping == 1 ? "1 a dormir" : $"{sleeping} a dormir");
        if (members.Count > MaxTiles) parts.Add($"a mostrar {MaxTiles}");
        return string.Join(" · ", parts);
    }

    // O cartão fica enquanto o rato estiver na etiqueta ou no próprio cartão.
    public static bool ShouldStay(bool overPill, bool overCard) => overPill || overCard;
}
