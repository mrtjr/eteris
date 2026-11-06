namespace Eteris.Browser.Tabs;

// Miniaturas para o cartão da aba: ~320 px de largura, só de páginas web.
public static class Thumbnails
{
    public const double TargetWidth = 320;

    public static double ScaleFor(double viewWidth) => viewWidth <= TargetWidth ? 1 : TargetWidth / viewWidth;

    public static bool ShouldCapture(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
