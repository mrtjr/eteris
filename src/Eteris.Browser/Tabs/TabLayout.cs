namespace Eteris.Browser.Tabs;

public static class TabLayout
{
    public const double MaxTabWidth = 232;
    public const double MinTabWidth = 72;

    // Posição horizontal (relativa ao início das abas) → índice onde a aba arrastada deve ficar.
    public static int IndexAt(double x, double tabWidth, int count)
        => count <= 0 ? 0 : Math.Clamp((int)Math.Floor(x / tabWidth), 0, count - 1);

    public static double TabWidth(double available, int count)
    {
        if (count <= 0) return MaxTabWidth;
        return Math.Clamp(Math.Floor(available / count), MinTabWidth, MaxTabWidth);
    }
}
