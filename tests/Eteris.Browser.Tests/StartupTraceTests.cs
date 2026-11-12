using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// A medição do arranque (ETERIS_TRACE_STARTUP) só existe com a variável: sem ela não se escreve nada.
public class StartupTraceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    public void Resolve_IsOffWithoutTheVariable(string? value)
        => Assert.Null(StartupTrace.Resolve(value, 42));

    [Fact]
    public void Resolve_OneWritesToTempWithTheProcessId()
        => Assert.Equal(Path.Combine(Path.GetTempPath(), "eteris-trace-42.tsv"), StartupTrace.Resolve("1", 42));

    [Fact]
    public void Resolve_PathKeepsItAndReplacesThePid()
        => Assert.Equal(@"C:\m\run-42.tsv", StartupTrace.Resolve(@" C:\m\run-{pid}.tsv ", 42));

    [Fact]
    public void ParseSeconds_KeepsOnlyPositiveNumbers()
        => Assert.Equal(new HashSet<int> { 20, 60 }, StartupTrace.ParseSeconds("20, 60,x,-3,0"));

    [Fact]
    public void ParseSeconds_EmptyWithoutTheVariable()
        => Assert.Empty(StartupTrace.ParseSeconds(null));

    // Sem a variável (como nos testes), o Lazy medido é um Lazy normal e as marcas não fazem nada.
    [Fact]
    public void WithoutTheVariable_MarksAreNoOpsAndLazyStillWorks()
    {
        Assert.False(StartupTrace.IsOn);
        StartupTrace.Mark("x");
        StartupTrace.MarkOnce("x");
        StartupTrace.Snapshot("x");
        StartupTrace.Flush();
        var calls = 0;
        var lazy = StartupTrace.Lazy("x", () => ++calls);
        Assert.Equal(1, lazy.Value);
        Assert.Equal(1, lazy.Value);
    }

    // INF-11: instalado e com o perfil normal, a variável do rastro não vale (escrevia ficheiros no %TEMP% de quem a tivesse
    // esquecida no ambiente); com um perfil de teste, ou fora de uma instalação, vale.
    [Fact]
    public void ResolveFor_IsNullWhenInstalledWithTheDefaultProfile()
    {
        Assert.Null(StartupTrace.ResolveFor("1", 42, installed: true, customProfile: false));
        Assert.NotNull(StartupTrace.ResolveFor("1", 42, installed: true, customProfile: true));
        Assert.NotNull(StartupTrace.ResolveFor("1", 42, installed: false, customProfile: false));
        Assert.Null(StartupTrace.ResolveFor("0", 42, installed: false, customProfile: true));
    }
}
