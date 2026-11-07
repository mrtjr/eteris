using Eteris.Browser.Browser;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// «Terminar sessão privada»: a confirmação diz o que fecha; as escolhas de http:// feitas lá acabam com ela.
public class PrivateSessionTests
{
    [Theory]
    [InlineData(1, 0, "Terminar a sessão privada? Fecha a janela privada e apaga os cookies, a cache e o armazenamento dela.")]
    [InlineData(1, 1, "Terminar a sessão privada? Fecha a janela privada (1 aba) e apaga os cookies, a cache e o armazenamento dela.")]
    [InlineData(2, 5, "Terminar a sessão privada? Fecha as 2 janelas privadas (5 abas) e apaga os cookies, a cache e o armazenamento delas.")]
    public void Confirmation_SaysWhatCloses(int windows, int tabs, string expected)
        => Assert.Equal(expected, PrivateSession.Confirmation(windows, tabs));

    [Fact]
    public void Done_CountsTheWindows()
    {
        Assert.Contains("a janela privada foi fechada", PrivateSession.Done(1));
        Assert.Contains("3 janelas privadas fechadas", PrivateSession.Done(3));
    }

    [Fact]
    public void ForgetPrivate_DropsOnlyThePrivateHttpChoices()
    {
        var https = new HttpsFirst();
        https.Allow("velho.example", isPrivate: true);
        https.Allow("normal.example", isPrivate: false);
        https.ForgetPrivate();
        Assert.NotNull(https.Upgrade("http://velho.example/", isPrivate: true));
        Assert.Null(https.Upgrade("http://normal.example/", isPrivate: false));
    }

    // As novidades desta fase estão na página «Novidades», com o atalho que existe na folha.
    [Fact]
    public void WhatsNew_AnnouncesTheBlockBHighlights()
    {
        Assert.Contains(WhatsNewPage.Highlights, h => h.Title == "HTTPS primeiro, com aviso");
        Assert.Contains(WhatsNewPage.Highlights, h => h.Title == "Anular" && h.Keys == "Ctrl+Z");
        Assert.Contains(WhatsNewPage.Highlights, h => h.Title == "Esquecer e triturar" && h.Detail.Contains("Terminar sessão privada"));
    }
}
