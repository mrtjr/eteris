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
}
