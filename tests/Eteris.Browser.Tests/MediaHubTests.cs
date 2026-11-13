using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class MediaHubTests
{
    [Fact]
    public void Parse_PlayingWithMetadata()
    {
        var info = MediaInfo.Parse("""{"s":"playing","a":true,"t":" Clair de Lune ","r":"Debussy"}""");
        Assert.Equal(new MediaInfo(MediaState.Playing, true, "Clair de Lune", "Debussy"), info);
    }

    [Fact]
    public void Parse_PausedIsNeverAudibleAndEmptyTextIsNull()
    {
        var info = MediaInfo.Parse("""{"s":"paused","a":true,"t":"","r":""}""");
        Assert.Equal(new MediaInfo(MediaState.Paused, false, null, null), info);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    public void Parse_NothingWhenNoMedia(string? raw) => Assert.Same(MediaInfo.Nothing, MediaInfo.Parse(raw));

    [Fact]
    public void Parse_TrimsVeryLongTitles()
        => Assert.Equal(120, MediaInfo.Parse("{\"s\":\"playing\",\"t\":\"" + new string('x', 400) + "\"}").Title!.Length);

    // Sem título da Media Session nem da página, o site (o nome da linha para o leitor de ecrã nunca fica vazio).
    [Fact]
    public void Tab_WithoutAnyTitle_HeadlineIsTheSite()
    {
        var tab = new BrowserTab("https://www.radio.pt/ao-vivo") { Title = " " };
        Assert.Equal("radio.pt", tab.MediaHeadline);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tab.Address = "https://outra.pt/";
        Assert.Contains(nameof(BrowserTab.MediaHeadline), raised);
        Assert.Equal("outra.pt", tab.MediaHeadline);
    }

    [Fact]
    public void Tab_ShowsMediaTitleOrFallsBackToPageTitle()
    {
        var tab = new BrowserTab("https://www.radio.pt/ao-vivo") { Title = "Rádio ao vivo" };
        Assert.Equal("Rádio ao vivo", tab.MediaHeadline);
        Assert.Equal("radio.pt", tab.MediaSubline);
        tab.ApplyMedia(new MediaInfo(MediaState.Playing, true, "Noticiário", "Antena 1"));
        Assert.Equal("Noticiário", tab.MediaHeadline);
        Assert.Equal("Antena 1 · radio.pt", tab.MediaSubline);
        Assert.True(tab.IsAudible);
        Assert.Equal(MediaState.Playing, tab.Media);
    }

    [Fact]
    public void Tab_GlyphsFollowState()
    {
        var tab = new BrowserTab("https://a.pt/");
        tab.ApplyMedia(new MediaInfo(MediaState.Playing, true, null, null));
        var pause = tab.PlayPauseGlyph;
        tab.ApplyMedia(new MediaInfo(MediaState.Paused, false, null, null));
        Assert.NotEqual(pause, tab.PlayPauseGlyph);
        var loud = tab.MuteGlyph;
        tab.IsMuted = true;
        Assert.NotEqual(loud, tab.MuteGlyph);
    }
}
