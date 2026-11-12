using System.Collections.Concurrent;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// DAD-07: a sessão grava-se fora do fio da janela (a descarga para o disco custa milissegundos); só a última versão
// interessa e nunca há duas escritas ao mesmo tempo.
public class LatestWriterTests
{
    [Fact]
    public void Post_WhileWriting_SkipsToTheLatest()
    {
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<string>();
        var writer = new LatestWriter<string>(value =>
        {
            written.Enqueue(value);
            if (value == "a") { first.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            return true;
        });
        writer.Post("a");
        Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
        writer.Post("b");
        writer.Post("c");
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => written.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(["a", "c"], written);
    }

    [Fact]
    public async Task WriteNow_DropsThePendingAndWritesLast()
    {
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<string>();
        var writer = new LatestWriter<string>(value =>
        {
            written.Enqueue(value);
            if (value == "a") { first.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            return value != "d";
        });
        var failed = 0;
        writer.Failed += () => Interlocked.Increment(ref failed);
        writer.Post("a");
        Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
        writer.Post("b");
        var now = Task.Run(() => writer.WriteNow("d"));
        Assert.True(SpinWait.SpinUntil(() => !writer.HasPending, TimeSpan.FromSeconds(10)));
        release.Set();
        Assert.False(await now);
        Assert.Equal(["a", "d"], written);
        Assert.Equal(1, failed);
    }

    // Uma escrita que lança (nunca deve) conta como falha em vez de derrubar o processo numa thread do pool.
    [Fact]
    public void AThrowingWrite_CountsAsFailed()
    {
        var failed = 0;
        var writer = new LatestWriter<string>(_ => throw new InvalidOperationException());
        writer.Failed += () => Interlocked.Increment(ref failed);
        Assert.False(writer.WriteNow("x"));
        Assert.Equal(1, failed);
    }
}
