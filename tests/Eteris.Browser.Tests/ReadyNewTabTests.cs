using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// E12 do plano de desempenho da 0.2.14 (ajuste e12, desligado por omissão: regra R-2): a 1.ª nova aba preparada em fundo
// no arranque, e o settings.json e o session.json lidos com o código gerado — com o mesmo resultado que a leitura por
// reflexão, também nos ficheiros das versões anteriores.
public class ReadyNewTabTests
{
    private static readonly DateTime At = new(2026, 11, 9, 10, 0, 0);

    [Fact]
    public void Tweak_IsOffByDefault()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.ReadyNewTab);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
    }

    // ---------- A nova aba pré-gerada ----------

    [Fact]
    public void Ready_IsUsedWithTheSameChoicesShortlyAfter()
    {
        var prefs = new UserSettings();
        var ready = new NewTabPrecompute.Ready("<html>", prefs, "DuckDuckGo", 0, At);
        Assert.True(NewTabPrecompute.Valid(ready, prefs, "DuckDuckGo", At.AddSeconds(3), 0));
    }

    [Fact]
    public void Ready_IsDroppedWhenAnythingChanged()
    {
        var prefs = new UserSettings();
        var ready = new NewTabPrecompute.Ready("<html>", prefs, "DuckDuckGo", 0, At);
        // Outra escolha nas definições (outro record, mesmo que igual), outro motor, bloqueios entretanto, tarde demais,
        // outro dia, ou um relógio que andou para trás.
        Assert.False(NewTabPrecompute.Valid(ready, prefs with { }, "DuckDuckGo", At.AddSeconds(3), 0));
        Assert.False(NewTabPrecompute.Valid(ready, prefs, "Brave Search", At.AddSeconds(3), 0));
        Assert.False(NewTabPrecompute.Valid(ready, prefs, "DuckDuckGo", At.AddSeconds(3), 2));
        Assert.False(NewTabPrecompute.Valid(ready, prefs, "DuckDuckGo", At + NewTabPrecompute.MaxAge + TimeSpan.FromSeconds(1), 0));
        var lateNight = ready with { At = new DateTime(2026, 11, 9, 23, 59, 50) };
        Assert.False(NewTabPrecompute.Valid(lateNight, prefs, "DuckDuckGo", new DateTime(2026, 11, 10, 0, 0, 5), 0));
        Assert.False(NewTabPrecompute.Valid(ready, prefs, "DuckDuckGo", At.AddSeconds(-1), 0));
    }

    // O aviso da rede de segurança do sandbox só se decide no Prepare, depois de a cópia começar (App.OnStartup): uma cópia
    // sem o aviso não serve um arranque com ele, nem o contrário (fusão com o versao-0.2.14).
    [Fact]
    public void Ready_IsDroppedWhenTheSandboxNoticeDiffers()
    {
        var prefs = new UserSettings();
        var notice = SandboxFallback.NoticeText(SandboxNotice.DisabledAfterFailures);
        var plain = new NewTabPrecompute.Ready("<html>", prefs, "DuckDuckGo", 0, At);
        Assert.False(NewTabPrecompute.Valid(plain, prefs, "DuckDuckGo", At.AddSeconds(3), 0, notice));
        var withNotice = plain with { Notice = notice };
        Assert.True(NewTabPrecompute.Valid(withNotice, prefs, "DuckDuckGo", At.AddSeconds(3), 0, notice));
        Assert.False(NewTabPrecompute.Valid(withNotice, prefs, "DuckDuckGo", At.AddSeconds(3), 0));
    }

    // Sem nada começado (o ajuste desligado), o pedido gera como sempre.
    [Fact]
    public void WithoutAStart_NothingIsTaken()
        => Assert.Null(NewTabPrecompute.Take(new UserSettings(), "DuckDuckGo", At, 0));

    // A cópia só serve a nova aba do perfil (nunca o ramo privado) e só começa com o ajuste, fora de um arranque privado.
    [Fact]
    public void Ready_IsOnlyForTheProfileNewTab()
    {
        var factory = Source(Path.Combine("Browser", "EterisSchemeHandlerFactory.cs"));
        var branch = factory[factory.IndexOf("if (isPrivate)", StringComparison.Ordinal)..];
        var take = branch.IndexOf("NewTabPrecompute.Take(", StringComparison.Ordinal);
        Assert.True(take > branch.IndexOf("else", StringComparison.Ordinal));
        Assert.Contains("html = ready ?? NewTabPrecompute.Render(engine, now, prefs, blocked, notice);", factory);
        Assert.Contains("NewTabPrecompute.Take(prefs, engine.Name, now, blocked, notice);", factory);
        Assert.Contains("if (PerfTuning.IsOn(PerfTuning.ReadyNewTab) && !StartupArgs.IsPrivate(e.Args)) Browser.NewTabPrecompute.Start(() => DateTime.Now);",
            Source("App.xaml.cs"));
    }

    // ---------- Leitura com o código gerado: igual à da reflexão ----------

    public static IEnumerable<object[]> SettingsFiles() =>
    [
        ["{}"],
        // 0.2.9 e anteriores: o pt-PT gravado por omissão, sem SiteLanguageChosen.
        ["{\"SearchEngine\":\"brave\",\"SleepMinutes\":5,\"SiteLanguage\":\"pt-PT\",\"Theme\":\"light\"}"],
        // Campos desconhecidos (de versões futuras ou tirados) ignorados; a ordem não conta.
        ["{\"Theme\":\"system\",\"Removido\":true,\"Accent\":\"coral\",\"Mica\":false,\"ArchiveDays\":14,\"ArchiveConfirmed\":true}"],
        // Nomes noutra capitalização não contam (leitura sensível a maiúsculas, como sempre).
        ["{\"theme\":\"light\",\"SearchEngine\":\"startpage\"}"],
        [JsonSerializer.Serialize(new UserSettings { Sidebar = true, SidebarWidth = 420, ReaderSize = 22, SecureDns = "quad9", ClearOnExit = true },
            new JsonSerializerOptions { WriteIndented = true })],
    ];

    [Theory]
    [MemberData(nameof(SettingsFiles))]
    public void Settings_ReadTheSameWithGeneratedCode(string json)
        => Assert.Equal(JsonSerializer.Deserialize<UserSettings>(json), JsonSerializer.Deserialize(json, StartupJsonContext.Default.UserSettings));

    public static IEnumerable<object[]> SessionFiles() =>
    [
        // Sessão antiga, só com os endereços e a ativa.
        ["{\"Urls\":[\"https://a.pt/\",\"eteris://newtab/\"],\"ActiveIndex\":1}"],
        // Com títulos, fixadas, grupos, ecrã dividido, identidades e Espaços (o que a SessionStore grava hoje).
        ["{\"Urls\":[\"https://a.pt/\",\"https://b.pt/\",\"https://c.pt/\"],\"ActiveIndex\":2,\"Titles\":[\"A\",\"B\",\"C\"],"
         + "\"Pinned\":[true,false,false],\"GroupIds\":[-1,0,0],\"Groups\":[{\"Name\":\"G\",\"ColorIndex\":3,\"Collapsed\":true}],"
         + "\"Split\":{\"Left\":0,\"Right\":1,\"Orientation\":\"side\"},"
         + "\"Metas\":[{\"Name\":\"x\",\"Emoji\":\"🙂\",\"Color\":2,\"Note\":\"n\",\"Locked\":true,\"Seen\":1730000000000},{},{}],"
         + "\"Spaces\":[{\"Name\":\"Casa\",\"ColorIndex\":1},{\"Name\":\"Trabalho\",\"ColorIndex\":4,\"Icon\":\"💼\",\"Left\":1730000000000,"
         + "\"Tabs\":{\"Urls\":[\"https://d.pt/\"],\"ActiveIndex\":0}}],\"SpaceIndex\":1}"],
        // Campos a null e desconhecidos.
        ["{\"Urls\":[\"https://a.pt/\"],\"ActiveIndex\":0,\"Titles\":null,\"Split\":null,\"Futuro\":{\"x\":1}}"],
    ];

    [Theory]
    [MemberData(nameof(SessionFiles))]
    public void Session_ReadsTheSameWithGeneratedCode(string json)
    {
        // As listas comparam-se pelo conteúdo: volta-se a escrever as duas leituras da mesma forma.
        var reflection = JsonSerializer.Serialize(JsonSerializer.Deserialize<Session>(json));
        var generated = JsonSerializer.Serialize(JsonSerializer.Deserialize(json, StartupJsonContext.Default.Session));
        Assert.Equal(reflection, generated);
    }

    // Um ficheiro estragado falha da mesma forma (os Load apanham a JsonException e arrancam sem ele).
    [Theory]
    [InlineData("{\"SleepMinutes\":\"muito\"}")]
    [InlineData("{\"Theme\":")]
    public void BrokenSettings_FailTheSameWay(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserSettings>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, StartupJsonContext.Default.UserSettings));
    }

    [Fact]
    public void Stores_ReadThroughTheTweak()
    {
        Assert.Contains("AtomicFile.ReadText(path, Read, out var outcome)", Source(Path.Combine("Data", "UserSettings.cs")));
        Assert.Contains("json => Read(json) is { Urls: not null } read ? read : null", Source(Path.Combine("Data", "SessionStore.cs")));
        Assert.Equal(2, Regex.Matches(Source(Path.Combine("Data", "UserSettings.cs")) + Source(Path.Combine("Data", "SessionStore.cs")),
            @"PerfTuning\.IsOn\(Infrastructure\.PerfTuning\.ReadyNewTab\)").Count);
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
