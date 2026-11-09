using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Tests;

// E6 do plano de desempenho da 0.2.14 (ajuste e6): o Espreitar larga o motor (e o contexto) 60 s depois de fechar, e os
// sites fixados escondidos na barra lateral adormecem como as abas, salvo «Manter acordado».
public class ReleaseGlanceTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Twenty = TimeSpan.FromMinutes(20);

    [Fact]
    public void HiddenPanel_SleepsAfterTheTabThreshold()
    {
        Assert.False(WebPanels.ShouldSleep(keepAwake: false, Now - TimeSpan.FromMinutes(19), Now, Twenty));
        Assert.True(WebPanels.ShouldSleep(keepAwake: false, Now - TimeSpan.FromMinutes(21), Now, Twenty));
    }

    [Fact]
    public void ShownPanel_NeverSleeps()
        => Assert.False(WebPanels.ShouldSleep(keepAwake: false, hiddenSinceUtc: null, Now, Twenty));

    [Fact]
    public void KeepAwake_NeverSleeps()
        => Assert.False(WebPanels.ShouldSleep(keepAwake: true, Now - TimeSpan.FromHours(5), Now, Twenty));

    // «Nunca» adormecer as abas (0 nas Definições) vale também para os sites fixados.
    [Fact]
    public void ZeroThreshold_MeansNever()
        => Assert.False(WebPanels.ShouldSleep(keepAwake: false, Now - TimeSpan.FromHours(5), Now, TimeSpan.Zero));

    [Fact]
    public void KeepAwake_IsSavedAndReadBack()
    {
        var list = WebPanels.SetKeepAwake([new WebPanel("https://chat.exemplo.pt/", "Chat")], "https://chat.exemplo.pt/", true);
        var json = WebPanels.Serialize(list);
        Assert.Contains("\"keepAwake\": true", json);
        Assert.True(Assert.Single(WebPanels.Parse(json)).KeepAwake);
    }

    // Os ficheiros de antes (sem o campo) leem-se iguais, e sem «Manter acordado» o campo nem se escreve.
    [Fact]
    public void OldFiles_ReadWithoutKeepAwake()
    {
        var parsed = Assert.Single(WebPanels.Parse("[{\"url\":\"https://chat.exemplo.pt/\",\"title\":\"Chat\",\"mobile\":true}]"));
        Assert.False(parsed.KeepAwake);
        Assert.True(parsed.Mobile);
        Assert.DoesNotContain("keepAwake", WebPanels.Serialize([parsed]));
    }

    [Fact]
    public void SetKeepAwake_OnAnUnknownSite_LeavesTheList()
    {
        IReadOnlyList<WebPanel> list = [new WebPanel("https://chat.exemplo.pt/", "Chat")];
        Assert.Same(list, WebPanels.SetKeepAwake(list, "https://outro.exemplo.pt/", true));
    }

    // O plano faz o E6 depender do D6, e os sites fixados escondidos na barra aberta ficavam acordados de propósito (um chat
    // não perde o sítio): desligado por omissão até ao D6.
    [Fact]
    public void Tweak_IsOffByDefaultUntilD6()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.ReleaseGlance);
        Assert.False(tweak.OnByDefault);
        Assert.Equal("D6", tweak.Waits);
    }

    // Como as abas: um site fixado com som, multimédia a tocar ou captura (uma chamada) não adormece; sem resposta da página
    // também não; e volta a confirmar, depois da resposta, que continua escondido e é o mesmo motor.
    [Fact]
    public void Panels_KeepTheTabExemptions()
    {
        var code = Source("MainWindow.WebPanels.cs");
        Assert.Contains("_ = SleepWebPanelUnlessBusyAsync(url);", code);
        Assert.DoesNotMatch(@"threshold\)\) continue;\s*ReleaseWebPanelBrowser\(url\);", code);
        Assert.Matches(@"_webPanelBrowsers\.TryGetValue\(url, out var browser\) \|\| IsCapturing\(browser\)", code);
        Assert.Contains("if (!result.Success) return;", code);
        Assert.Contains("if (media.Audible || media.State == global::Eteris.Browser.Tabs.MediaState.Playing) return;", code);
        Assert.Matches(@"!ReferenceEquals\(same, browser\) \|\| !_webPanelHiddenSince\.ContainsKey\(url\)", code);
    }

    // Sem o ajuste, «Manter acordado» não faria nada: os dois menus só o mostram com ele.
    [Fact]
    public void KeepAwakeItem_OnlyWithTheTweak()
        => Assert.Equal(2, Regex.Matches(Source("MainWindow.WebPanels.cs"),
            @"if \(PerfTuning\.IsOn\(PerfTuning\.ReleaseGlance\)\) menu\.Item\([^\n]*""Manter acordado""").Count);

    [Fact]
    public void Glance_ReleasesAMinuteAfterClosing()
        => Assert.Equal(TimeSpan.FromSeconds(60), MainWindow.GlanceRelease);

    // Ligações: fechar agenda a largada, reabrir cancela-a, a largada não acontece com o cartão aberto e larga também o
    // contexto; o fecho da janela não volta a libertar um motor já largado.
    [Fact]
    public void Glance_IsWired()
    {
        var code = Source("MainWindow.Glance.cs");
        Assert.Matches(@"ReturnFocusToPage\(\);\s*ScheduleGlanceRelease\(\);", code);
        Assert.Matches(@"_glanceUrl = url;\s*_glanceReleaseTimer\?\.Stop\(\);", code);
        Assert.Matches(@"private void ReleaseGlance\(\)\s*\{\s*_glanceReleaseTimer\?\.Stop\(\);\s*if \(GlanceOpen\) return;", code);
        Assert.Matches(@"_glanceContext\?\.Dispose\(\);\s*_glanceContext = null;", code);
        Assert.Contains("if (!browser.IsDisposed) browser.Dispose();", code);
        Assert.Contains("PerfTuning.IsOn(Infrastructure.PerfTuning.ReleaseGlance)", code);
    }

    [Fact]
    public void Panels_AreWired()
    {
        Assert.Contains("SleepIdleWebPanels(now, threshold);", Source("MainWindow.Sleep.cs"));
        var code = Source("MainWindow.WebPanels.cs");
        Assert.Contains("if (_sidebarOpen && _sidebarPanel?.Url == url) continue;", code);
        Assert.Matches(@"private void ReleaseWebPanelBrowser\(string url\)\s*\{\s*_webPanelHiddenSince\.Remove\(url\);", code);
        Assert.Equal(2, Regex.Matches(code, "\"Manter acordado\"").Count);
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
