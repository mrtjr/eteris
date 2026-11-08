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
}
