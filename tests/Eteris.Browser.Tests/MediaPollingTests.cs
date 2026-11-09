using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// E4 do plano de desempenho da 0.2.14 (ajuste e4): a central de multimédia deixa de acordar todas as abas de 2 em 2 s,
// e o modo foco deixa de ler o rato de 60 em 60 ms com a janela inativa.
public class MediaPollingTests
{
    private static readonly TimeSpan Now = TimeSpan.FromMinutes(10);

    [Fact]
    public void SeenTabs_ArePolledEveryRound()
        => Assert.True(MediaPolling.Due(seen: true, MediaState.None, audible: false, last: Now - MediaPolling.Round, nudged: false, Now));

    [Theory]
    [InlineData(MediaState.Playing, false)]
    [InlineData(MediaState.Paused, false)]
    [InlineData(MediaState.None, true)]
    public void TabsWithMediaOrSound_ArePolledEveryRound(MediaState media, bool audible)
        => Assert.True(MediaPolling.Due(seen: false, media, audible, last: Now - MediaPolling.Round, nudged: false, Now));

    [Fact]
    public void QuietHiddenTabs_WaitForTheBackgroundInterval()
    {
        Assert.False(MediaPolling.Due(false, MediaState.None, false, Now - TimeSpan.FromSeconds(29), false, Now));
        Assert.True(MediaPolling.Due(false, MediaState.None, false, Now - MediaPolling.Background, false, Now));
    }

    [Fact]
    public void ANudge_PollsAQuietHiddenTabAtOnce()
        => Assert.True(MediaPolling.Due(false, MediaState.None, false, Now - TimeSpan.FromSeconds(1), nudged: true, Now));

    [Fact]
    public void ANewTab_IsPolledOnce()
        => Assert.True(MediaPolling.Due(false, MediaState.None, false, last: null, nudged: false, Now));

    [Fact]
    public void Poll_ClearsTheNudge()
    {
        var poll = new MediaPolling.Poll();
        poll.Nudge();
        Assert.True(poll.Nudged);
        poll.Polled(Now);
        Assert.False(poll.Nudged);
        Assert.Equal(Now, poll.Last);
    }

    // Com 20 abas escondidas e paradas, uma ronda de 2 s passa de 20 sondagens a 0 (e 1 por aba a cada 30 s).
    [Fact]
    public void TwentyQuietTabs_AreNotWokenEveryRound()
    {
        var polls = Enumerable.Range(0, 20).Select(_ => new MediaPolling.Poll()).ToList();
        foreach (var p in polls) p.Polled(Now);
        var woken = 0;
        for (var t = Now + MediaPolling.Round; t < Now + MediaPolling.Background; t += MediaPolling.Round)
            woken += polls.Count(p => MediaPolling.Due(false, MediaState.None, false, p.Last, p.Nudged, t));
        Assert.Equal(0, woken);
    }

    [Fact]
    public void Tweak_IsOnByDefault()
        => Assert.True(Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.MediaPolling).OnByDefault);

    // Ligações no MainWindow: a ronda usa a regra com o ajuste; carregamento acabado e título novo empurram.
    [Fact]
    public void Window_UsesThePolicyAndTheNudges()
    {
        var audio = Source("MainWindow.Audio.cs");
        Assert.Contains("PerfTuning.IsOn(PerfTuning.MediaPolling)", audio);
        Assert.Contains("MediaPolling.Due(MediaSeen(tab, browser), tab.Media, tab.IsAudible, poll.Last, poll.Nudged, now)", audio);
        Assert.Matches(@"ReferenceEquals\(tab, Tabs\.Active\)\s*\|\|\s*_split\?\.PaneOf\(tab\) is not null\s*\|\|\s*_warmPages\.ContainsKey\(browser\)", audio);
        var window = Source("MainWindow.xaml.cs");
        Assert.Matches(@"browser\.TitleChanged \+= \(_, e\) =>\s*\{\s*NudgeMediaPoll\(tab\);", window);
        Assert.Contains("if (!e.IsLoading) NudgeMediaPoll(tab);", window);
    }

    // Modo foco: com a janela inativa e as barras escondidas, o temporizador de 60 ms pára; o Activated retoma-o.
    [Fact]
    public void AutoHide_StopsWhileInactiveAndResumesOnActivate()
    {
        var code = Source("MainWindow.AutoHide.cs");
        Assert.Matches(@"_autoHideResumesOnActivate && !IsActive && !before\)\s*\{\s*StopAutoHideTimer\(\);", code);
        Assert.Matches(@"Activated \+= \(_, _\) => SyncAutoHide\(\);", code);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
