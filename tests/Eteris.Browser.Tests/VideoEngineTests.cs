using Eteris.Browser.Tabs;
using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser.Tests;

public class VideoEngineTests
{
    [Theory]
    [InlineData("\u0001eteris-media format", true)]
    [InlineData("\u0001eteris-media patented-only", true)]
    [InlineData("\u0001eteris-media drm weak top", true)]
    [InlineData("\u0001eteris-media frame-player weak top", true)]
    [InlineData("\u0001eteris-media outra", false)]
    [InlineData("\u0001eteris-media format forte top", false)]
    [InlineData("\u0001eteris-media format strong top extra", false)]
    [InlineData("\u0001eteris-media format strong top no", true)]
    [InlineData("\u0001eteris-media format strong top no extra", false)]
    [InlineData("eteris-media format", false)]
    [InlineData(null, false)]
    public void IsReport_OnlyKnownReasons(string? message, bool expected) => Assert.Equal(expected, VideoEngine.IsReport(message));

    [Theory]
    [InlineData("format strong top", "format", true, false)]
    [InlineData("patented-only weak frame", "patented-only", false, true)]
    [InlineData("format", "format", true, false)] // formato antigo: forte, no topo
    public void Parse_ReadsReasonStrengthAndOrigin(string body, string reason, bool strong, bool frame)
        => Assert.Equal(new VideoEngine.VideoReport(reason, strong, frame), VideoEngine.Parse(VideoEngine.ReportPrefix + body));

    [Fact]
    public void Parse_ReadsWhetherTheEngineSaidNo()
    {
        Assert.True(VideoEngine.Parse(VideoEngine.ReportPrefix + "patented-only strong top no")!.EngineSaidNo);
        Assert.False(VideoEngine.Parse(VideoEngine.ReportPrefix + "patented-only strong top")!.EngineSaidNo);
        Assert.Null(VideoEngine.Parse(VideoEngine.ReportPrefix + "format strong top sim"));
    }

    // Motor com codecs (Tabs/PlatformCodecs.cs): só conta o aviso de um motor que respondeu «não» ao H.264/AAC.
    [Theory]
    [InlineData("format", true, false, false, VideoEngine.Decision.None)]          // MP4 estragado: não é o formato
    [InlineData("patented-only", true, false, false, VideoEngine.Decision.None)]
    [InlineData("frame-player", false, false, false, VideoEngine.Decision.None)]   // o leitor do Vimeo toca no motor principal
    [InlineData("drm", true, false, false, VideoEngine.Decision.Offer)]            // DRM: continua a ser uma pergunta
    [InlineData("format", true, false, true, VideoEngine.Decision.Switch)]         // sem GPU: o motor disse «não»
    [InlineData("format", false, false, true, VideoEngine.Decision.Offer)]
    [InlineData("frame-player", false, false, true, VideoEngine.Decision.Offer)]
    [InlineData("format", true, true, true, VideoEngine.Decision.Offer)]           // o utilizador mandou o site voltar
    public void Decide_WithPlatformCodecs_OnlyWhenTheEngineSaidNo(string reason, bool strong, bool optedOut, bool saidNo,
        VideoEngine.Decision expected)
    {
        var decision = VideoEngine.Decide(new VideoEngine.VideoReport(reason, strong, false, saidNo), optedOut, mainHasCodecs: true);
        Assert.Equal(expected, decision);
    }

    [Fact]
    public void Decide_WithoutPlatformCodecs_IgnoresWhetherTheEngineSaidNo()
    {
        foreach (var saidNo in new[] { false, true })
            Assert.Equal(VideoEngine.Decision.Switch, VideoEngine.Decide(new VideoEngine.VideoReport("format", true, false, saidNo), false));
    }

    [Theory]
    [InlineData("format", true, false, false, VideoEngine.Decision.Switch)]
    [InlineData("patented-only", true, false, false, VideoEngine.Decision.Switch)]
    [InlineData("format", false, false, false, VideoEngine.Decision.Offer)]   // vídeo de fundo (Amazon)
    [InlineData("format", true, true, false, VideoEngine.Decision.Offer)]    // leitor num iframe
    [InlineData("format", true, false, true, VideoEngine.Decision.Offer)]    // o utilizador mandou o site voltar
    [InlineData("drm", true, false, false, VideoEngine.Decision.Offer)]      // DRM: sempre com pergunta
    [InlineData("frame-player", true, false, false, VideoEngine.Decision.Offer)]
    public void Decide_SwitchesOnlyOnStrongSignalsInTheTopDocument(string reason, bool strong, bool frame, bool optedOut,
        VideoEngine.Decision expected)
        => Assert.Equal(expected, VideoEngine.Decide(new VideoEngine.VideoReport(reason, strong, frame), optedOut));

    [Fact]
    public void Decide_OnlyOffersOnReddit()
        // O Reddit pede reCAPTCHA no motor de vídeo: só com o botão.
        => Assert.Equal(VideoEngine.Decision.Offer, VideoEngine.Decide(new VideoEngine.VideoReport("format", true, false), false, "reddit.com"));

    [Theory]
    [InlineData("twitch.tv", false, false, true)]   // lista embutida
    [InlineData("twitch.tv", false, true, false)]   // excluído pelo utilizador: a lista não o força
    [InlineData("twitch.tv", true, true, false)]
    [InlineData("exemplo.pt", true, false, true)]   // lembrado depois de uma troca
    [InlineData("exemplo.pt", false, false, false)]
    [InlineData("youtube.com", false, false, false)] // AV1/VP9: fica no motor principal
    [InlineData("reddit.com", false, false, false)]  // misto: reCAPTCHA no motor de vídeo
    public void OpensInVideoEngine_KnownSitesUnlessOptedOut(string site, bool remembered, bool optedOut, bool expected)
        => Assert.Equal(expected, VideoEngine.OpensInVideoEngine(site, remembered, optedOut));

    // Com codecs no motor principal, a lista embutida deixa de abrir sites no motor de vídeo; o que o utilizador
    // lembrou nesse modo (lista própria em AppData) continua a valer.
    [Theory]
    [InlineData("twitch.tv", false, false, false)]
    [InlineData("vimeo.com", false, false, false)]
    [InlineData("tiktok.com", false, false, false)]
    [InlineData("exemplo.pt", true, false, true)]
    [InlineData("twitch.tv", true, true, false)]
    public void OpensInVideoEngine_WithPlatformCodecs_IgnoresTheBuiltInList(string site, bool remembered, bool optedOut, bool expected)
        => Assert.Equal(expected, VideoEngine.OpensInVideoEngine(site, remembered, optedOut, mainHasCodecs: true));

    [Fact]
    public void WatchScript_SaysWhenTheEngineItselfRefusesH264OrAac()
    {
        // A pergunta do próprio script usa a função original (guardada antes de a envolver) e os mesmos perfis da sonda.
        var script = VideoEngine.WatchScript;
        Assert.Contains("const nativeSupported = window.MediaSource && MediaSource.isTypeSupported;", script);
        Assert.Contains("engineSays('video/mp4; codecs=\"avc1.640028\"')", script);
        Assert.Contains("engineSays('audio/mp4; codecs=\"mp4a.40.2\"')", script);
        Assert.True(script.IndexOf("const nativeSupported", StringComparison.Ordinal) < script.IndexOf("type.isTypeSupported = function", StringComparison.Ordinal));
        Assert.Contains("+ said);", script);
    }

    [Fact]
    public void WatchScript_CatchesTheVideoThatPlaysOnlyTheSound()
    {
        // Compilação própria sem descodificação por hardware: a faixa H.264 de um MP4 é descartada sem erro.
        var script = VideoEngine.WatchScript;
        Assert.Contains("v.readyState >= 1 && v.videoWidth === 0", script);
        Assert.Contains("&& lacksVideo();", script);
        Assert.Contains("if (noPicture(m)) return report('format', strength(m));", script);
    }

    [Fact]
    public void WatchScript_DoesNotCountHevcProbesAsPatented()
    {
        // O Twitch offline trocava de motor só por sondar hvc1: a regex do que o site precisa não inclui HEVC/DV.
        var needed = System.Text.RegularExpressions.Regex.Match(VideoEngine.WatchScript, @"const needed = /([^/]+)/i;").Groups[1].Value;
        Assert.Equal("avc1|avc3|mp4a|h264|aac", needed);
        Assert.DoesNotContain("window.top !== window) return", VideoEngine.WatchScript);
    }

    [Fact]
    public void WatchScript_KeepsItsOwnConsoleChannel()
        // O g1 substitui o console.debug: o canal é guardado no arranque do script.
        => Assert.Contains("const debug = console.debug.bind(console);", VideoEngine.WatchScript);

    [Theory]
    [InlineData("https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", null, null, true)]
    [InlineData("https://a.pt/som.m4a?x=1", "application/octet-stream", null, true)]
    [InlineData("https://a.pt/stream", "application/vnd.apple.mpegURL", null, true)]
    [InlineData("https://a.pt/video", "video/mp4; codecs=avc1", null, true)]
    [InlineData("https://a.pt/video.mp4", "video/mp4", "attachment; filename=video.mp4", false)] // o site pediu para guardar
    [InlineData("https://a.pt/doc.pdf", "application/pdf", null, false)]
    [InlineData("https://a.pt/arquivo.zip", "application/zip", null, false)]
    [InlineData("blob:https://a.pt/123", "video/mp4", null, false)] // gerado pela página: é para guardar
    [InlineData(null, "video/mp4", null, false)]
    public void PlayableDownloads_GoToTheVideoEngine(string? url, string? mime, string? disposition, bool expected)
        => Assert.Equal(expected, VideoEngine.IsPlayableDownload(url, mime, disposition));

    [Theory]
    [InlineData("https://a.pt/v.mp4", 20.7, "https://a.pt/v.mp4#t=20")]
    [InlineData("https://a.pt/v.mp4#t=3", 61.2, "https://a.pt/v.mp4#t=61")]
    public void TimeFragment_ResumesDirectMedia(string url, double seconds, string expected)
        => Assert.Equal(expected, VideoEngine.WithTimeFragment(url, seconds));

    [Theory]
    [InlineData("https://a.pt/v.mp4", true)]
    [InlineData("https://a.pt/watch?v=1", false)]
    public void DirectMedia_ByExtension(string url, bool expected) => Assert.Equal(expected, VideoEngine.IsDirectMedia(url));

    [Theory]
    [InlineData(CoreWebView2WebResourceContext.Script, "script")]
    [InlineData(CoreWebView2WebResourceContext.Image, "image")]
    [InlineData(CoreWebView2WebResourceContext.Fetch, "xmlhttprequest")]
    [InlineData(CoreWebView2WebResourceContext.Document, "document")]
    [InlineData(CoreWebView2WebResourceContext.Manifest, "other")]
    public void ToAdblock_UsesFilterListNames(CoreWebView2WebResourceContext context, string expected)
        => Assert.Equal(expected, VideoEngine.ToAdblock(context));

    [Fact]
    public void EdgeEngine_StartsWithoutSmartScreenOrBackgroundTraffic()
    {
        var arguments = VideoEngine.BrowserArguments(blockAutoplay: false);
        Assert.Contains("msSmartScreenProtection", arguments);
        Assert.Contains("--disable-background-networking", arguments);
        // O component updater do Edge descarregava componentes sem ação do utilizador (diagnóstico de vídeo).
        Assert.Contains("--disable-component-update", arguments);
        Assert.DoesNotContain("autoplay-policy", arguments);
    }

    [Fact]
    public void EdgeEngine_FollowsTheAutoplaySetting()
        => Assert.Contains("--autoplay-policy=document-user-activation-required", VideoEngine.BrowserArguments(blockAutoplay: true));

    [Fact]
    public void Blocker_SkipsMediaSegments()
    {
        // Cada segmento de vídeo passava pela thread da janela; os outros tipos continuam filtrados.
        Assert.False(VideoEngine.IsFiltered(CoreWebView2WebResourceContext.Media));
        Assert.True(VideoEngine.IsFiltered(CoreWebView2WebResourceContext.Script));
        Assert.True(VideoEngine.IsFiltered(CoreWebView2WebResourceContext.Document));
        Assert.True(VideoEngine.IsFiltered(CoreWebView2WebResourceContext.Fetch));
        Assert.True(VideoEngine.IsFiltered(CoreWebView2WebResourceContext.Ping));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/", true)]
    [InlineData("http://a.pt/", true)]
    [InlineData("eteris://newtab", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void OnlyWebAddressesGoToTheVideoEngine(string? url, bool expected) => Assert.Equal(expected, VideoEngine.CanHost(url));
}
