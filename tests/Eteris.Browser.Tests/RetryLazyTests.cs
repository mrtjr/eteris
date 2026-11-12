using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

// DAD-15: uma falha passageira ao abrir a base (outra ligação, o antivírus) ficava guardada no Lazy até o Éteris fechar,
// e favoritos e histórico apareciam vazios sem explicação. O RetryLazy volta a tentar passado um intervalo.
public class RetryLazyTests
{
    [Fact]
    public void Failure_IsRetriedAfterTheInterval_AndSuccessIsKept()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var lazy = new RetryLazy<string>(() => ++calls == 1 ? throw new IOException("ocupada") : "aberta", TimeSpan.FromSeconds(30), () => now);
        Assert.Null(lazy.Value);
        Assert.Null(lazy.Value);
        Assert.Equal(1, calls);
        Assert.Throws<IOException>(() => lazy.Required);
        Assert.False(lazy.IsValueCreated);
        now = now.AddSeconds(31);
        Assert.Equal("aberta", lazy.Value);
        Assert.True(lazy.IsValueCreated);
        now = now.AddHours(1);
        Assert.Equal("aberta", lazy.Required);
        Assert.Equal(2, calls);
        lazy.Reset();
        Assert.Equal("aberta", lazy.Value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Null_CountsAsFailure()
    {
        var lazy = new RetryLazy<string>(() => null, TimeSpan.Zero);
        Assert.Null(lazy.Value);
        Assert.Throws<InvalidOperationException>(() => lazy.Required);
    }

    // O Reset pode vir de dentro da criação (a base posta de lado reinicia todas as lojas, incluindo a que está a abrir):
    // o valor dessa criação devolve-se a quem o pediu mas não se guarda.
    [Fact]
    public void Reset_DuringCreation_DiscardsThatValue()
    {
        var calls = 0;
        RetryLazy<string>? lazy = null;
        lazy = new RetryLazy<string>(() =>
        {
            if (++calls == 1) lazy!.Reset();
            return "v" + calls;
        }, TimeSpan.Zero);
        Assert.Equal("v1", lazy.Value);
        Assert.False(lazy.IsValueCreated);
        Assert.Equal("v2", lazy.Value);
        Assert.True(lazy.IsValueCreated);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneCreation()
    {
        var calls = 0;
        var lazy = new RetryLazy<string>(() =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return "aberta";
        }, TimeSpan.FromSeconds(30));
        var results = Enumerable.Range(0, 8).Select(_ => Task.Run(() => lazy.Value)).ToArray();
        Assert.All(await Task.WhenAll(results), r => Assert.Equal("aberta", r));
        Assert.Equal(1, calls);
    }
}
