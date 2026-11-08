using Eteris.Browser.Browser;
using static Eteris.Browser.Browser.PageWindow;

namespace Eteris.Browser.Tests;

public class PageWindowTests
{
    [Fact]
    public void Static_NeverPaintsOverThePageBackdrop()
    {
        // Sem apagar nem pintar: fica a cor que o WPF desenhou por baixo (e nunca alfa 0, que deixava ver o Mica).
        Assert.Equal(Paint.SkipErase, PaintStep(0x0014));
        Assert.Equal(Paint.Validate, PaintStep(0x000F));
    }

    [Theory]
    [InlineData(0x0007)] // WM_SETFOCUS: o CefSharp passa o foco à página
    [InlineData(0x0021)] // WM_MOUSEACTIVATE
    [InlineData(0x0046)] // WM_WINDOWPOSCHANGING: o HwndHost acerta posição e tamanho
    [InlineData(0x003D)] // WM_GETOBJECT: acessibilidade
    [InlineData(0x0085)] // WM_NCPAINT: a static não tem moldura
    public void OtherMessages_FollowTheNormalPath(int message) => Assert.Equal(Paint.Pass, PaintStep(message));

    [Theory]
    [InlineData(1360.0, 777.0, 1.0, 1360, 777)]
    [InlineData(1360.6, 777.9, 1.0, 1360, 777)] // a parte inteira, como o CefSharp ((int)ActualWidth)
    [InlineData(1000.0, 601.0, 1.25, 1250, 751)] // 751,25 -> 751
    [InlineData(1001.0, 603.0, 1.5, 1502, 904)] // 1501,5 -> 1502 e 904,5 -> 904 (Math.Round: ao par, como o CefSharp)
    [InlineData(800.0, 600.0, 2.0, 1600, 1200)]
    [InlineData(800.0, 600.0, 0.0, 800, 600)] // sem escala conhecida: como a 100 %
    public void PixelSize_MatchesTheCefSharpResize(double width, double height, double scale, int expectedWidth, int expectedHeight)
        => Assert.Equal((expectedWidth, expectedHeight), PixelSize(width, height, scale));

    [Theory]
    [InlineData(double.NaN, 10.0)]
    [InlineData(-5.0, 10.0)]
    [InlineData(double.PositiveInfinity, 10.0)]
    public void PixelSize_InvalidLayout_IsZero(double width, double height) => Assert.Equal(0, PixelSize(width, height, 1.5).Width);

    [Fact]
    public void BirthSize_UsesThePageAreaWhenKnown()
    {
        // Aba nova: a static ainda é 0×0 (o WPF não a dispôs); a janela do CEF nasce já com o tamanho da área.
        Assert.Equal((1360, 777), BirthSize((0, 0), (1360, 777)));
        // Área ainda por dispor (janela a abrir) ou sem área: fica o que o CefSharp leu da static.
        Assert.Equal((640, 480), BirthSize((640, 480), (0, 777)));
        Assert.Equal((640, 480), BirthSize((640, 480), null));
    }

    [Fact]
    public void RestoreStep_ColdTab_OneStepAtTheFinalSize()
    {
        // Minimizada e com o tamanho final conhecido: SetWindowPlacement (uma só mudança de tamanho para o Chromium).
        Assert.Equal(Restore.Placement, RestoreStep(minimized: true, (1360, 777)));
        // Tamanho por saber: restaurar e deixar o CefSharp acertá-lo (como antes).
        Assert.Equal(Restore.ShowOnly, RestoreStep(minimized: true, (0, 777)));
        Assert.Equal(Restore.ShowOnly, RestoreStep(minimized: true, (1360, 0)));
        // Já à vista: nada (nunca mexer no tamanho de uma página que se vê).
        Assert.Equal(Restore.None, RestoreStep(minimized: false, (1360, 777)));
        Assert.Equal(Restore.None, RestoreStep(minimized: false, (0, 0)));
    }
}
