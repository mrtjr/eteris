namespace Eteris.Browser.Palette;

// O CEF mede o zoom em "níveis" (fator = 1.2^nível); a interface usa os passos percentuais do Chrome.
public static class ZoomSteps
{
    private static readonly double[] Factors = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    public static double Next(double current, int direction)
    {
        if (direction > 0)
            return Factors.FirstOrDefault(f => f > current + 0.001, Factors[^1]);
        return Factors.LastOrDefault(f => f < current - 0.001, Factors[0]);
    }

    public static double ToLevel(double factor) => Math.Log(factor) / Math.Log(1.2);

    public static double ToFactor(double level) => Math.Pow(1.2, level);

    public static int Percent(double level) => (int)Math.Round(ToFactor(level) * 100);
}
