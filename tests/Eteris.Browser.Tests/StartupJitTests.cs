using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O perfil do JIT do arranque fica na pasta do perfil do Éteris, criada se ainda não existir (1.º arranque).
public sealed class StartupJitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-jit-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Start_CreatesTheProfileFolder()
    {
        StartupJit.Start(_root);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void FileName_IsInsideTheProfileAndNotUserData()
        => Assert.Equal("arranque.jitprofile", StartupJit.FileName);

    // Um arranque privado não grava o perfil do JIT (diria que funções se usaram na sessão privada).
    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "https://exemplo.pt/" }, true)]
    [InlineData(new[] { "--private" }, false)]
    [InlineData(new[] { "--incognito", "https://exemplo.pt/" }, false)]
    public void ShouldRecord_NotInAPrivateStart(string[] args, bool record)
        => Assert.Equal(record, StartupJit.ShouldRecord(args));

    // A gravação para pouco depois da 1.ª página, não no fim do processo.
    [Fact]
    public void RecordAfterFirstPaint_IsShort()
        => Assert.InRange(StartupJit.RecordAfterFirstPaint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));

    // A janela privada e o fim do arranque chamam Stop sem saber se já foi chamado: pode repetir-se.
    [Fact]
    public void Stop_CanBeCalledMoreThanOnce()
    {
        StartupJit.Stop();
        StartupJit.Stop();
    }
}
