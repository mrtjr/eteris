using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class HttpsFirstTests
{
    [Theory]
    [InlineData("http://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://www.publico.pt/", "https://www.publico.pt/")]
    [InlineData("HTTP://Example.com", "https://example.com/")]
    public void Upgrade_PublicHttpBecomesHttps(string url, string expected)
        => Assert.Equal(expected, new HttpsFirst().Upgrade(url));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://meu-router/")]
    [InlineData("http://example.com:8080/")]
    [InlineData("eteris://newtab/")]
    public void Upgrade_LeavesLocalCustomPortAndSecureAlone(string url)
        => Assert.Null(new HttpsFirst().Upgrade(url));

    // Já não há recuo silencioso: a falha devolve o http:// original (para o aviso), mas o host continua a ser
    // subido na visita seguinte até a pessoa escolher continuar.
    [Fact]
    public void FailedUpgrade_ReturnsTheOriginal_ButDoesNotAllowHttp()
    {
        var https = new HttpsFirst();
        https.Upgrade("http://insecure.example/x");
        Assert.Equal("http://insecure.example/x", https.FailedUpgrade("https://insecure.example/x"));
        Assert.Equal("https://insecure.example/y", https.Upgrade("http://insecure.example/y"));
    }

    [Fact]
    public void FailedUpgrade_OnlyForUrlsThatWereUpgraded()
    {
        var https = new HttpsFirst();
        https.Upgrade("http://upgraded.example/");
        Assert.Null(https.FailedUpgrade("https://never-upgraded.example/"));
        Assert.Equal("http://upgraded.example/", https.FailedUpgrade("https://upgraded.example/"));
        // Duas abas do mesmo endereço a falhar: as duas têm o aviso; outro endereço do host não subido, não.
        Assert.Equal("http://upgraded.example/", https.FailedUpgrade("https://upgraded.example/"));
        Assert.Null(https.FailedUpgrade("https://upgraded.example/outra"));
    }

    // A marca recua uma vez: passada a janela das abas irmãs, a mesma falha já não devolve o http:// (sem isto, um
    // http:// que o servidor manda de volta para o https:// que falha recarregava sem fim).
    [Fact]
    public void FailedUpgrade_FallsBackOnlyOncePerUpgrade()
    {
        var now = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var https = new HttpsFirst(clock: () => now);
        https.Upgrade("http://loja.example/");
        Assert.Equal("http://loja.example/", https.FailedUpgrade("https://loja.example/"));
        now += HttpsFirst.SiblingWindow + TimeSpan.FromSeconds(1);
        Assert.Null(https.FailedUpgrade("https://loja.example/"));
        // Uma subida nova volta a ter direito ao aviso.
        https.Upgrade("http://loja.example/");
        Assert.Equal("http://loja.example/", https.FailedUpgrade("https://loja.example/"));
    }

    // As subidas da janela privada ficam à parte e acabam com a sessão privada.
    [Fact]
    public void FailedUpgrade_PrivateMarksAreSeparate_AndForgottenWithTheSession()
    {
        var https = new HttpsFirst();
        https.Upgrade("http://segredo.example/a", isPrivate: true);
        Assert.Null(https.FailedUpgrade("https://segredo.example/a", isPrivate: false));
        https.ForgetPrivate();
        Assert.Null(https.FailedUpgrade("https://segredo.example/a", isPrivate: true));
        https.Upgrade("http://segredo.example/b", isPrivate: true);
        Assert.Equal("http://segredo.example/b", https.FailedUpgrade("https://segredo.example/b", isPrivate: true));
    }

    // O motor de vídeo pergunta o https:// sem marcar a subida.
    [Fact]
    public void Secure_GivesTheHttpsFormWithoutMarking()
    {
        var https = new HttpsFirst();
        Assert.Equal("https://video.example/canal", https.Secure("http://video.example/canal"));
        Assert.Null(https.FailedUpgrade("https://video.example/canal"));
        Assert.Null(https.Secure("https://video.example/"));
        https.Allow("video.example", isPrivate: false);
        Assert.Null(https.Secure("http://video.example/canal"));
    }

    [Fact]
    public void Allow_KeepsHttpForTheSession_AndForgetUndoesIt()
    {
        var https = new HttpsFirst();
        https.Allow("velho.example", isPrivate: false);
        Assert.Null(https.Upgrade("http://velho.example/a"));
        // Só o host aceite: outro subdomínio continua a ir por https://.
        Assert.NotNull(https.Upgrade("http://loja.velho.example/"));
        https.Forget("velho.example");
        Assert.NotNull(https.Upgrade("http://velho.example/a"));
    }

    // O que se aceita na janela privada fica lá (e vice-versa).
    [Fact]
    public void Allow_PrivateAndNormalAreSeparate()
    {
        var https = new HttpsFirst();
        https.Allow("segredo.example", isPrivate: true);
        Assert.Null(https.Upgrade("http://segredo.example/", isPrivate: true));
        Assert.NotNull(https.Upgrade("http://segredo.example/", isPrivate: false));
    }

    // «Sempre para este site»: a exceção guardada vale nas duas janelas.
    [Fact]
    public void SavedException_SkipsTheUpgrade()
    {
        var https = new HttpsFirst(host => host == "sempre.example");
        Assert.Null(https.Upgrade("http://sempre.example/"));
        Assert.Null(https.Upgrade("http://sempre.example/", isPrivate: true));
        Assert.NotNull(https.Upgrade("http://outro.example/"));
    }

    [Theory]
    [InlineData(-102, true)]
    [InlineData(-7, true)]
    [InlineData(-107, true)]
    [InlineData(-118, true)]
    [InlineData(-200, true)]
    [InlineData(-105, false)]
    [InlineData(-137, false)]
    [InlineData(-106, false)]
    [InlineData(-3, false)]
    [InlineData(-6, false)]
    // Respostas que só o servidor https:// estraga: o http:// ainda pode funcionar.
    [InlineData(-310, true)]
    [InlineData(-320, true)]
    [InlineData(-324, true)]
    // Também impediriam o http://: endereço inalcançável e proxy em baixo.
    [InlineData(-109, false)]
    [InlineData(-130, false)]
    [InlineData(-131, false)]
    [InlineData(-21, false)]
    public void UpgradeFailure_OnlyWhenHttpCouldStillWork(int errorCode, bool expected)
        => Assert.Equal(expected, HttpsFirst.IsUpgradeFailure(errorCode));
}
