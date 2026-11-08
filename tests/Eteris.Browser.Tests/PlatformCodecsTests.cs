using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Motor com codecs da plataforma (Tabs/PlatformCodecs.cs, docs/motor.md «Motor com codecs»): a escolha do motor no
// arranque. Sem a compilação própria tudo fica como antes; com ela, a sonda pode ainda dizer «não» (sem GPU, Windows N).
public class PlatformCodecsTests
{
    [Fact]
    public void WithoutTheOwnBuild_NothingChanges()
    {
        Assert.Equal(PlatformCodecs.Support.Missing, PlatformCodecs.Initial(ownRuntime: false));
        Assert.False(PlatformCodecs.HasCodecs(PlatformCodecs.Initial(ownRuntime: false)));
    }

    [Fact]
    public void WithTheOwnBuild_CodecsAreAssumedUntilTheProbeAnswers()
    {
        Assert.Equal(PlatformCodecs.Support.Unprobed, PlatformCodecs.Initial(ownRuntime: true));
        Assert.True(PlatformCodecs.HasCodecs(PlatformCodecs.Support.Unprobed));
        Assert.True(PlatformCodecs.HasCodecs(PlatformCodecs.Support.Present));
    }

    [Theory]
    [InlineData(true, "h264 aac", PlatformCodecs.Support.Present)]
    [InlineData(true, "- aac", PlatformCodecs.Support.Missing)]    // sem descodificação por hardware: o motor diz «não» ao avc1
    [InlineData(true, "h264 -", PlatformCodecs.Support.Missing)]   // Windows N sem o Media Feature Pack: sem AAC
    [InlineData(true, "- -", PlatformCodecs.Support.Missing)]
    [InlineData(true, "sem-mse", PlatformCodecs.Support.Missing)]
    [InlineData(true, null, PlatformCodecs.Support.Unprobed)]      // resposta estranha: volta a perguntar
    [InlineData(false, null, PlatformCodecs.Support.Unprobed)]     // a página fechou / tempo esgotado
    public void Probe_OnlyBothCodecsCount(bool success, string? answer, PlatformCodecs.Support expected)
        => Assert.Equal(expected, PlatformCodecs.FromProbe(success, answer));

    [Fact]
    public void Probe_AsksForH264HighAndAacLcThroughMediaSource()
    {
        Assert.Contains("isTypeSupported('video/mp4; codecs=\"avc1.640028\"')", PlatformCodecs.ProbeScript);
        Assert.Contains("isTypeSupported('audio/mp4; codecs=\"mp4a.40.2\"')", PlatformCodecs.ProbeScript);
    }

    [Theory]
    [InlineData("eteris://newtab", true)]
    [InlineData("eteris://settings", true)]
    [InlineData("https://www.twitch.tv/", false)] // nunca numa página da web: o WatchScript e o site veriam a pergunta
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void Probe_RunsOnlyOnInternalPages(string? url, bool expected) => Assert.Equal(expected, PlatformCodecs.IsProbePage(url));

    [Fact]
    public void Marker_IsTheManifestTheProjectCopiesNextToTheLicenses()
        => Assert.Equal(System.IO.Path.Combine("licenses", "cef-codecs", "manifest.json"), PlatformCodecs.MarkerPath);

    // A escolha do motor, de ponta a ponta, nos três casos medidos ao vivo (docs/motor.md).
    [Theory]
    // Runtime do NuGet: o Twitch abre logo no motor de vídeo e um MP4 que falha troca.
    [InlineData(false, null, "twitch.tv", true, "format strong top no", VideoEngine.Decision.Switch)]
    // Compilação própria com GPU: o Twitch fica no motor principal e um erro de vídeo já não troca.
    [InlineData(true, "h264 aac", "twitch.tv", false, "format strong top", VideoEngine.Decision.None)]
    // Compilação própria sem GPU: o MP4 H.264 tocava só o som — a sonda diz «não» e tudo volta a ser como sem codecs.
    [InlineData(true, "- aac", "twitch.tv", true, "format strong top no", VideoEngine.Decision.Switch)]
    // Compilação própria antes da sonda: a lista não abre, mas o aviso do motor que disse «não» troca.
    [InlineData(true, null, "twitch.tv", false, "format strong top no", VideoEngine.Decision.Switch)]
    public void EngineChoice_EndToEnd(bool ownRuntime, string? probe, string site, bool opensInVideoEngine, string report,
        VideoEngine.Decision decision)
    {
        var support = PlatformCodecs.Initial(ownRuntime);
        if (ownRuntime && probe is not null) support = PlatformCodecs.FromProbe(true, probe);
        var codecs = PlatformCodecs.HasCodecs(support);
        Assert.Equal(opensInVideoEngine, VideoEngine.OpensInVideoEngine(site, remembered: false, optedOut: false, codecs));
        var parsed = VideoEngine.Parse(VideoEngine.ReportPrefix + report)!;
        Assert.Equal(decision, VideoEngine.Decide(parsed, optedOut: false, "w3schools.com", codecs));
    }
}
