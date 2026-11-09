using Eteris.Browser.Browser;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// «Terminar sessão privada»: a confirmação diz o que fecha; as escolhas de http:// feitas lá acabam com ela.
public class PrivateSessionTests
{
    [Theory]
    [InlineData(1, 0, "Terminar a sessão privada? Fecha a janela privada e apaga os cookies, a cache, o armazenamento e as permissões dela.")]
    [InlineData(1, 1, "Terminar a sessão privada? Fecha a janela privada (1 aba) e apaga os cookies, a cache, o armazenamento e as permissões dela.")]
    [InlineData(2, 5, "Terminar a sessão privada? Fecha as 2 janelas privadas (5 abas) e apaga os cookies, a cache, o armazenamento e as permissões delas.")]
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

    // O HTTPS primeiro está nas «Novidades» da 0.2.9 (junto da rede local); o Anular e o Esquecer e triturar ficam no
    // CHANGELOG.md (as Novidades levam só os 6–8 destaques da versão).
    [Fact]
    public void WhatsNew_AnnouncesHttpsFirst()
    {
        Assert.Contains(WhatsNewPage.Highlights.Skip(10).Take(8), h => h.Detail.Contains("HTTPS", StringComparison.Ordinal));
    }
}
