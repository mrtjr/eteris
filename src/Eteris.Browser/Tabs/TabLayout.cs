namespace Eteris.Browser.Tabs;

public static class TabLayout
{
    public const double MaxTabWidth = 232;
    public const double MinTabWidth = 72;

    public static double TabWidth(double available, int count)
    {
        if (count <= 0) return MaxTabWidth;
        return Math.Clamp(Math.Floor(available / count), MinTabWidth, MaxTabWidth);
    }
}
