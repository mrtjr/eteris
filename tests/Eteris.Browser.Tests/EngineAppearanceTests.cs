using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests;

// Proposta 3 («Pele nova», 3.3 (3)): as funcionalidades do motor só de aspeto (Shields/EngineAppearance.cs). Por omissão
// nada que mude o que os sites medem entra na linha de comandos; as barras sobrepostas só com a opção das Definições,
// pelo mesmo Merge, e então aparecem em eteris://privacy/.
public class EngineAppearanceTests
{
    private static Dictionary<string, string> Args(bool overlay = false)
        => new(CefConfigurator.Build("p", overlayScrollbars: overlay).CefCommandLineArgs);

    [Fact]
    public void All_ListsTheOverlayScrollbars_AsChangingTheFingerprint()
    {
        var overlay = Assert.Single(EngineAppearance.All, f => f.Feature == "OverlayScrollbar");
        Assert.True(overlay.ChangesFingerprint);
        Assert.False(overlay.Disable);
        Assert.Contains(EngineAppearance.All, f => f.Feature == "OverlayScrollbarFlashWhenMouseEnter");
        Assert.All(EngineAppearance.All, f => Assert.False(string.IsNullOrWhiteSpace(f.What)));
        // As barras Fluent não existem no motor 156 (EngineFeatureNamesTests confirma-o): não entram.
        Assert.DoesNotContain(EngineAppearance.All, f => f.Feature.Contains("Fluent", StringComparison.Ordinal));
    }

    [Fact]
    public void ByDefault_NothingThatChangesTheFingerprintReachesTheCommandLine()
    {
        Assert.False(new UserSettings().OverlayScrollbars);
        var args = Args();
        Assert.Empty(EngineAppearance.ActiveIn(args));
        var switched = EngineHardening.Switch(args, "enable-features").Concat(EngineHardening.Switch(args, "disable-features"));
        Assert.DoesNotContain(switched, n => EngineAppearance.All.Any(f => f.ChangesFingerprint && f.Feature == n));
    }

    [Fact]
    public void WithTheOption_JoinsByMerge_WithoutErasingWhatIsThere()
    {
        var args = new Dictionary<string, string> { ["enable-features"] = "A,B", ["disable-features"] = "C" };
        EngineAppearance.Apply(args, overlayScrollbars: true);
        Assert.Equal(["A", "B", "OverlayScrollbar", "OverlayScrollbarFlashWhenMouseEnter"], EngineHardening.Switch(args, "enable-features"));
        Assert.Equal(["C"], EngineHardening.Switch(args, "disable-features"));

        var untouched = new Dictionary<string, string> { ["enable-features"] = "A" };
        EngineAppearance.Apply(untouched, overlayScrollbars: false);
        Assert.Equal("A", untouched["enable-features"]);
    }

    [Fact]
    public void WithTheOption_TheEngineStartsWithTheOverlayScrollbars_NextToEverythingElse()
    {
        var args = Args(overlay: true);
        var enabled = EngineHardening.Switch(args, "enable-features");
        Assert.Contains("OverlayScrollbar", enabled);
        // As promessas continuam lá (o Merge não substitui o enable-features).
        Assert.All(EngineHardening.Partitioning, p => Assert.Contains(p.Feature, enabled));
        Assert.Equal(EngineAppearance.All.Select(f => f.Feature), EngineAppearance.ActiveIn(args).Select(f => f.Feature));
    }

    // ActiveIn lê a linha de comandos com que o motor arrancou, não as definições.
    [Fact]
    public void ActiveIn_ReadsTheRealCommandLine()
    {
        Assert.Empty(EngineAppearance.ActiveIn(new Dictionary<string, string>()));
        var only = EngineAppearance.ActiveIn(new Dictionary<string, string> { ["enable-features"] = "X,OverlayScrollbar" });
        Assert.Equal("OverlayScrollbar", Assert.Single(only).Feature);
        Assert.Empty(EngineAppearance.ActiveIn(new Dictionary<string, string> { ["disable-features"] = "OverlayScrollbar" }));
    }

    // O arranque passa a escolha das Definições ao motor (como o «Bloquear vídeos com som automático»).
    [Fact]
    public void Prepare_PassesTheSettingToTheEngine()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "CefConfigurator.cs"));
        Assert.Contains("overlayScrollbars: Data.Settings.Current.OverlayScrollbars", code);
        var hardening = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "EngineHardening.cs"));
        var performance = hardening.IndexOf("EnginePerformance.Apply(args, Infrastructure.PerfTuning.IsOn);", StringComparison.Ordinal);
        var appearance = hardening.IndexOf("EngineAppearance.Apply(args, overlayScrollbars);", StringComparison.Ordinal);
        Assert.True(performance > 0 && appearance > performance && appearance < hardening.IndexOf("Applied = ", StringComparison.Ordinal));
    }

    // A opção: desligada por omissão, só «true»/«false», com o aviso nas Definições; fica neste computador (não viaja
    // com a sincronização nem com o perfil exportado).
    [Fact]
    public void Setting_IsOffByDefault_Warns_AndStaysOnThisComputer()
    {
        Assert.True(new UserSettings().With("overlayScrollbars", "true")!.OverlayScrollbars);
        Assert.False(new UserSettings { OverlayScrollbars = true }.With("overlayScrollbars", "false")!.OverlayScrollbars);
        Assert.Null(new UserSettings().With("overlayScrollbars", "True"));
        Assert.DoesNotContain("overlayScrollbars", ProfileTransfer.SettingValues(new UserSettings { OverlayScrollbars = true }).Keys);

        Assert.Contains("\"overlayScrollbars\":true", SettingsPage.Render(new UserSettings { OverlayScrollbars = true }));
        Assert.Contains("\"overlayScrollbars\":false", SettingsPage.Render(new UserSettings()));
        var html = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "settings.html"));
        var at = html.IndexOf("data-key=\"overlayScrollbars\"", StringComparison.Ordinal);
        Assert.True(at > html.IndexOf("<section id=\"aspeto\">", StringComparison.Ordinal) && at < html.IndexOf("<section id=\"arranque\">", StringComparison.Ordinal));
        var item = html[html.LastIndexOf("<div class=\"item\">", at, StringComparison.Ordinal)..at];
        Assert.Contains("<b>Barras de deslocamento sobrepostas (finas, por cima do conteúdo)</b>", item);
        Assert.Contains("muda o que os sites conseguem medir", item);
        Assert.Contains("Aplica-se da próxima vez que abrir o Éteris.", item);
        // Revisão: com «kb» o interruptor fica no Tab e o aria-label chega ao leitor de ecrã (.switch input é display:none;
        // .switch.kb input fica focável, invisível, com o anel no desenho do interruptor).
        Assert.Contains("<label class=\"switch kb\"><input type=\"checkbox\" data-key=\"overlayScrollbars\" aria-label=\"Barras de deslocamento sobrepostas\">", html);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
