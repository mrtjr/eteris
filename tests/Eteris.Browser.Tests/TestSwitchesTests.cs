using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// VAU-08, INF-11, EGR-11, ARQ-12: os interruptores de teste só valem num perfil de teste (--user-data-dir) e a via de teste
// da confirmação do Windows não vai no binário publicado.
public class TestSwitchesTests
{
    private static string Source(string relative) => GoogleSyncTests.Src("Eteris.Browser", relative);

    [Fact]
    public void DevToolsAndMute_AreIgnoredInTheDefaultProfile()
    {
        Assert.Null(TestSwitches.DevToolsPortFor("9333", customProfile: false));
        Assert.Equal(9333, TestSwitches.DevToolsPortFor("9333", customProfile: true));
        Assert.Null(TestSwitches.DevToolsPortFor("80", customProfile: true));
        Assert.False(TestSwitches.MuteFor("1", customProfile: false));
        Assert.True(TestSwitches.MuteFor("1", customProfile: true));
        Assert.False(TestSwitches.MuteFor("0", customProfile: true));
    }

    // Cada variável ETERIS_* do código tem a sua regra declarada: quem acrescentar uma tem de a pôr aqui, e dizer onde vale.
    [Fact]
    public void EveryEterisVariable_HasADeclaredRule()
    {
        var found = Directory.EnumerateFiles(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(ETERIS_[A-Z_]+)\"").Select(m => m.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["ETERIS_DEMO_UPDATE", "ETERIS_TEST_AJUSTES", "ETERIS_TEST_CONTADORES", "ETERIS_TEST_CRASH_BEFORE_PAINT",
            "ETERIS_TEST_DEVTOOLS_PORT", "ETERIS_TEST_ENERGIA", "ETERIS_TEST_HOST_RULES", "ETERIS_TEST_MUTE", "ETERIS_TEST_PRESSAO", "ETERIS_TEST_PURO",
            "ETERIS_TEST_REAUTH", "ETERIS_TEST_SYNC_FOLDER", "ETERIS_TRACE_GC_AT", "ETERIS_TRACE_STARTUP"], found);
        var switches = Source(Path.Combine("Infrastructure", "TestSwitches.cs"));
        Assert.Matches(@"DevToolsPort\s*=>\s*DevToolsPortFor\(Environment\.GetEnvironmentVariable\(""ETERIS_TEST_DEVTOOLS_PORT""\),\s*AppPaths\.IsCustomProfile\)", switches);
        Assert.Matches(@"Mute\s*=>\s*MuteFor\(Environment\.GetEnvironmentVariable\(""ETERIS_TEST_MUTE""\),\s*AppPaths\.IsCustomProfile\)", switches);
    }

    [Fact]
    public void ReauthHook_IsCompiledOutOfThePublishedBinary()
    {
        var switches = Source(Path.Combine("Infrastructure", "TestSwitches.cs"));
        Assert.True(switches.IndexOf("#if ETERIS_TEST_HOOKS", StringComparison.Ordinal) < switches.IndexOf("\"ETERIS_TEST_REAUTH\"", StringComparison.Ordinal));
        Assert.Contains("#if ETERIS_TEST_HOOKS", Source(Path.Combine("Vault", "WindowsReauth.cs")));
        var publish = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "build", "publish.ps1"));
        Assert.Contains("-p:EterisTestHooks=false", publish);
        Assert.Contains("'ETERIS_TEST_REAUTH'", publish);
        Assert.Contains("ETERIS_TEST_HOOKS", File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Eteris.Browser.csproj")));
    }
}
