using System.IO;
using Eteris.Browser.Infrastructure;
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
    [Theory]
    [InlineData(null, "https://www.w3schools.com/html/mov_bbb.mp4", "mov_bbb.mp4")]
    [InlineData("", "https://www.w3schools.com/html/mov_bbb.mp4?x=1#t=3", "mov_bbb.mp4")]
    [InlineData("https://cdn.example.com/a%20b.webm", "https://cdn.example.com/a%20b.webm", "a b.webm")]
    [InlineData("about:blank", "https://cdn.example.com/", "cdn.example.com")]
    [InlineData("Big Buck Bunny", "https://cdn.example.com/v.mp4", "Big Buck Bunny")]
    [InlineData(null, "não é endereço", "não é endereço")]
    public void PageTitle_UntitledMediaUsesTheFileNameOrHost(string? documentTitle, string url, string expected)
        => Assert.Equal(expected, VideoEngine.PageTitle(documentTitle, url));

    // O motor principal recebe do Chromium o endereço como título de um ficheiro de média aberto diretamente: fica o
    // nome do ficheiro, o mesmo que o motor de vídeo grava (o histórico tinha «w3schools.com/html/mov_bbb.mp4» num e
    // «mov_bbb.mp4» no outro).
    [Theory]
    [InlineData("w3schools.com/html/mov_bbb.mp4", "https://www.w3schools.com/html/mov_bbb.mp4", "mov_bbb.mp4")]
    [InlineData("www.w3schools.com/html/mov_bbb.mp4", "https://www.w3schools.com/html/mov_bbb.mp4", "mov_bbb.mp4")]
    [InlineData("https://cdn.example.com/a%20b.mp3", "https://cdn.example.com/a%20b.mp3", "a b.mp3")]
    [InlineData("cdn.example.com/a b.mp3", "https://cdn.example.com/a%20b.mp3", "a b.mp3")]
    [InlineData(null, "https://cdn.example.com/v.webm", "v.webm")]
    [InlineData("Big Buck Bunny", "https://cdn.example.com/v.mp4", "Big Buck Bunny")]
    [InlineData("example.com/pagina", "https://example.com/pagina", "example.com/pagina")]
    [InlineData(null, "https://example.com/pagina", "https://example.com/pagina")]
    [InlineData("Notícias", "https://example.com/", "Notícias")]
    public void MainEngineTitle_DirectMediaUsesTheFileNameLikeTheVideoEngine(string? title, string url, string expected)
        => Assert.Equal(expected, VideoEngine.MainEngineTitle(title, url));

    [Fact]
    public void MainEngineTitle_MatchesTheVideoEngineForAnUntitledFile()
    {
        const string url = "https://www.w3schools.com/html/mov_bbb.mp4";
        Assert.Equal(VideoEngine.PageTitle(null, url), VideoEngine.MainEngineTitle("w3schools.com/html/mov_bbb.mp4", url));
    }

    [Fact]
    public void DataKinds_MapTheClearDataBoxes()
    {
        Assert.Equal(CoreWebView2BrowsingDataKinds.AllSite, VideoEngine.DataKinds(history: false, cookies: true, cache: false));
        Assert.Equal(CoreWebView2BrowsingDataKinds.DiskCache, VideoEngine.DataKinds(false, false, true));
        Assert.Equal(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory, VideoEngine.DataKinds(true, false, false));
        Assert.Equal((CoreWebView2BrowsingDataKinds)0, VideoEngine.DataKinds(false, false, false));
    }

    [Fact]
    public void MayHoldData_OnlyForSitesThatOpenedInTheVideoEngine()
    {
        Assert.True(VideoEngine.MayHoldData("twitch.tv", remembered: false, optedOut: false));
        Assert.True(VideoEngine.MayHoldData("exemplo.pt", remembered: true, optedOut: false));
        // O botão «Motor de vídeo» tira o site dos lembrados e põe-no nos excluídos: os dados dele continuam no WebView2.
        Assert.True(VideoEngine.MayHoldData("exemplo.pt", remembered: false, optedOut: true));
        Assert.False(VideoEngine.MayHoldData("exemplo.pt", remembered: false, optedOut: false));
    }

    [Fact]
    public void EveryCleanup_ReachesTheVideoEngineProfile()
    {
        string Src(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "src", "Eteris.Browser", .. parts]));
        Assert.Equal(Path.Combine(AppPaths.Root, "video-engine"), AppPaths.VideoEngine);
        Assert.Contains("userDataFolder: AppPaths.VideoEngine", Src("MainWindow.VideoEngine.cs"));
        Assert.Contains("if (!Directory.Exists(AppPaths.VideoEngine)) return true;", Src("MainWindow.VideoEngine.cs"));
        // Janela normal: com uma aba viva no motor de vídeo limpa-se sempre; sem ela, só os sites que lá podem ter dados.
        Assert.Contains("if (!IsPrivate && (LiveVideoCore() is not null || VideoEngine.MayHoldData(", Src("MainWindow.Forget.cs"));
        Assert.Contains("await ClearVideoSiteAsync(site, origins)", Src("MainWindow.Forget.cs"));
        Assert.Contains("if (!privateContext && !await MainWindow.ClearVideoEngineAsync(request).ConfigureAwait(false))", Src("Browser", "EterisSchemeHandlerFactory.cs"));
        // O DevTools do WebView2 só pelo canal (SandboxRulesTests).
        Assert.Contains("\"Storage.clearDataForOrigin\"", Src("Browser", "VideoPageScript.cs"));
        Assert.Contains("VideoPageScript.ClearOriginAsync(core, origin)", Src("MainWindow.VideoEngine.cs"));
        Assert.Contains("ProfileCleanup.RemoveVideoEngineData(AppPaths.VideoEngine);", Src("Infrastructure", "CefConfigurator.cs"));
        var exit = Src("App.xaml.cs");
        Assert.True(exit.IndexOf("ProfileCleanup.RemoveVideoEngineData(AppPaths.VideoEngine)", StringComparison.Ordinal) > exit.IndexOf("CefConfigurator.Shutdown();", StringComparison.Ordinal));
    }

    // Uma limpeza do motor de vídeo nunca fica pendurada: tempo-limite, controlador tardio fechado, ambiente reposto
    // quando o controlador não se cria, e as origens recusadas chegam ao aviso.
    [Fact]
    public void VideoProfileCleanup_HasATimeLimit_AndSaysWhatStayed()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.VideoEngine.cs"));
        Assert.Equal(TimeSpan.FromSeconds(15), VideoEngine.CleanupLimit);
        Assert.Contains("await Task.WhenAny(work, Task.Delay(VideoEngine.CleanupLimit))", code);
        Assert.Contains("if (expired.IsCancellationRequested) return false;", code);
        var create = code.IndexOf("controller = await environment.CreateCoreWebView2ControllerAsync(", StringComparison.Ordinal);
        var reset = code.IndexOf("s_videoEnvironment = null;", create, StringComparison.Ordinal);
        Assert.True(create > 0 && reset > create);
        Assert.Contains("if (!await VideoPageScript.ClearOriginAsync(core, origin)) refused++;", code);
        Assert.Contains("if (!video.Complete) videoFailed = true;", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs")));
    }

    // PRO-04, EGR-03, EGR-14: as abas do motor de vídeo levam o GPC, a língua do motor principal, a política do WebRTC e as
    // notificações recusadas, e os relatórios de falha ficam no computador.
    [Fact]
    public void BrowserArguments_CarryTheWebRtcPolicy()
        => Assert.Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp", VideoEngine.BrowserArguments(blockAutoplay: false));

    [Theory]
    [InlineData("en-US,en", "en-US")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("", "pt-PT")]
    public void Language_IsTheOneTheMainEngineStartedWith(string started, string expected) => Assert.Equal(expected, VideoEngine.Language(started));

    [Fact]
    public void VideoTabs_CarryGpcTheSiteLanguageAndLocalCrashReports()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.VideoEngine.cs"));
        var handler = code.IndexOf("core.WebResourceRequested += (_, e) =>", StringComparison.Ordinal);
        var gpc = code.IndexOf("foreach (var (name, value) in PrivacySignals.Headers) e.Request.Headers.SetHeader(name, value);", StringComparison.Ordinal);
        Assert.True(handler > 0 && gpc > handler && gpc < code.IndexOf("var context = e.ResourceContext;", StringComparison.Ordinal));
        var script = code.IndexOf("await VideoPageScript.AddEarlyAsync(core, PrivacySignals.SessionScript);", StringComparison.Ordinal);
        Assert.True(script > 0 && script < code.IndexOf("core.Navigate(VideoUrl(url));", StringComparison.Ordinal));
        Assert.Contains("language: VideoEngine.Language(EngineHardening.StartedWithLanguage)", code);
        Assert.DoesNotContain("language: \"pt-PT\"", code);
        Assert.Contains("IsCustomCrashReportingEnabled = true", code);
        Assert.Contains("CoreWebView2PermissionKind.Notifications", code);
        Assert.Contains("CoreWebView2PermissionState.Deny", code);
        Assert.Contains("tab?.UsesVideoEngine == true", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Shield.cs")));
    }

    // Medido (docs/paridade.md §7, 2026-11-12, WebView2 158.0.4370.16): sem o Page.enable, o Page.addScriptToEvaluateOnNewDocument
    // devolve um identificador mas o script nunca corre; com ele, corre antes dos scripts da página.
    [Fact]
    public void EarlyScripts_EnableThePageDomainFirst()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "VideoPageScript.cs"));
        var enable = code.IndexOf("CallDevToolsProtocolMethodAsync(\"Page.enable\", \"{}\")", StringComparison.Ordinal);
        var add = code.IndexOf("CallDevToolsProtocolMethodAsync(\"Page.addScriptToEvaluateOnNewDocument\"", StringComparison.Ordinal);
        Assert.True(enable > 0 && add > enable);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
