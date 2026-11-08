using Eteris.Browser.Tabs;
using static Eteris.Browser.Tabs.WarmTabs;

namespace Eteris.Browser.Tests;

public class WarmTabsTests
{
    // Leitura de um relógio monótono (MainWindow.WarmClock): só a diferença conta.
    private static readonly TimeSpan Now = TimeSpan.FromHours(30);

    private static Candidate<string> Left(string page, double secondsAgo, bool eligible = true, bool video = false)
        => new(page, Now - TimeSpan.FromSeconds(secondsAgo), eligible, video);

    [Fact]
    public void GraceIsShortAndTheLimitSmall()
    {
        // A exceção ao «abas escondidas não gastam» (docs/paridade.md #1b) tem de ser curta e para poucas abas.
        Assert.InRange(Grace, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        Assert.InRange(Limit, 1, 2);
        Assert.True(VideoCheckLimit <= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Keep_TheMostRecentWithinTheGrace()
    {
        var keep = Keep([Left("a", 1), Left("b", 3), Left("c", 0.5)], Now);
        Assert.Equal(["c", "a"], keep);
    }

    [Fact]
    public void Keep_NothingAfterTheGrace()
    {
        Assert.Empty(Keep([Left("a", Grace.TotalSeconds), Left("b", Grace.TotalSeconds + 5)], Now));
        Assert.Equal(["a"], Keep([Left("a", Grace.TotalSeconds - 0.01)], Now));
    }

    [Fact]
    public void Keep_NeverAPageWithVideoPlaying()
    {
        // Um vídeo a tocar fora de vista seria descodificado e desenhado para ninguém.
        Assert.Equal(["b"], Keep([Left("a", 0.1, video: true), Left("b", 2)], Now));
    }

    [Fact]
    public void Keep_NeverAnIneligiblePage()
    {
        // Ecrã dividido, janela minimizada, aba com som, a passar para o motor de vídeo (MainWindow.CanStayWarm).
        Assert.Empty(Keep([Left("a", 0.1, eligible: false)], Now));
    }

    [Fact]
    public void Keep_AReadingFromTheFuture_CoolsDown()
    {
        Assert.Empty(Keep([new Candidate<string>("a", Now + TimeSpan.FromSeconds(30), true, false)], Now));
    }

    [Fact]
    public void NextReview_WhenTheFirstOneLeavesTheGrace()
    {
        Assert.Equal(Grace - TimeSpan.FromSeconds(3), NextReview([Left("a", 1), Left("b", 3)], Now));
        Assert.Equal(TimeSpan.Zero, NextReview([Left("a", Grace.TotalSeconds + 1)], Now));
        Assert.Null(NextReview(Array.Empty<Candidate<string>>(), Now));
    }

    [Theory]
    [InlineData(true, false, false)] // só um «não» claro deixa a aba quente
    [InlineData(true, true, true)]
    [InlineData(true, null, true)]
    [InlineData(true, "false", true)]
    [InlineData(false, null, true)] // erro ou tempo esgotado: arrefece
    [InlineData(false, false, true)]
    public void PlaysVideo_OnlyAClearNoKeepsItWarm(bool success, object? result, bool expected)
        => Assert.Equal(expected, PlaysVideo(success, result));

    [Fact]
    public void PlayingVideoScript_LooksEverywhereItCan()
    {
        Assert.Contains("querySelectorAll('video')", PlayingVideoScript);
        Assert.Contains("shadowRoot", PlayingVideoScript);
        Assert.Contains("window.frames", PlayingVideoScript);
        Assert.Contains("!v.paused", PlayingVideoScript);
    }

    [Fact]
    public void PlayingVideoScript_AnEmbeddedPlayerFromAnotherSiteCountsAsPlaying()
    {
        // Nem o <video> nem o som de um iframe de outro site se deixam ver (IsAudible só lê o documento principal):
        // um iframe ilegível com tamanho e permissões de leitor arrefece a página, por cautela.
        Assert.Contains("contentDocument", PlayingVideoScript);
        Assert.Contains("allowfullscreen", PlayingVideoScript);
        Assert.Contains("indexOf('autoplay')", PlayingVideoScript);
        Assert.Contains($"box.width < {PlayerWidth} || box.height < {PlayerHeight}", PlayingVideoScript);
    }
}
