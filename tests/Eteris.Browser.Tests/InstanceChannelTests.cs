using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class InstanceChannelTests
{
    [Fact]
    public async Task Send_DeliversArgsToListener()
    {
        var name = "eteris-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string[]>();
        using var listener = InstanceChannel.Listen(name, args => received.TrySetResult(args));

        Assert.True(InstanceChannel.Send(name, ["https://www.publico.pt/", "--private"]));
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://www.publico.pt/", "--private"], args);
    }

    [Fact]
    public async Task Listener_HandlesSeveralMessages()
    {
        var name = "eteris-test-" + Guid.NewGuid().ToString("N");
        var count = 0;
        var done = new TaskCompletionSource();
        // Muitos envios seguidos: prova que o canal reabre entre mensagens, não a latência. Cada envio tem 15 s, porque
        // na bateria o pool de threads pode estar preso por testes síncronos; o prazo curto fica só em
        // Listen_PipeExistsWhenListenReturns.
        using var listener = InstanceChannel.Listen(name, _ => { if (Interlocked.Increment(ref count) == 10) done.TrySetResult(); });
        for (var i = 0; i < 10; i++) Assert.True(InstanceChannel.Send(name, [$"https://s{i}.pt/"], timeoutMs: 15_000), $"envio {i} falhou");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    // CI: «envio 0 falhou» ao fim dos 2 s — o pipe era criado dentro de um Task.Run e, com o pool de threads ocupado
    // pela bateria, ainda não existia. Agora existe quando Listen devolve: um envio logo a seguir, com um prazo curto
    // e sem nenhuma espera no meio, chega sempre.
    [Fact]
    public async Task Listen_PipeExistsWhenListenReturns()
    {
        var name = "eteris-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = InstanceChannel.Listen(name, args => received.TrySetResult(args));

        Assert.True(InstanceChannel.Send(name, ["https://www.rtp.pt/"], timeoutMs: 50), "o pipe ainda não existia");
        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://www.rtp.pt/"], args);
    }

    // INF-05: o Dispose pára mesmo a escuta (antes devolvia um CancellationTokenSource, que ao ser libertado não cancela
    // nada): um envio a seguir já não encontra o pipe e a mensagem não chega.
    [Fact]
    public async Task Listen_Dispose_StopsListening()
    {
        var name = "eteris-test-" + Guid.NewGuid().ToString("N");
        var called = 0;
        var listener = InstanceChannel.Listen(name, _ => Interlocked.Increment(ref called));
        listener.Dispose();
        await Task.Delay(300);
        Assert.False(InstanceChannel.Send(name, ["x"], timeoutMs: 300));
        Assert.Equal(0, called);
    }

    // O que o teste acima não vê (o ciclo antigo ficava a girar sem pipe estável): o Dispose cancela mesmo.
    [Fact]
    public void Listen_ReturnsAListenerWhoseDisposeCancels()
    {
        var source = Sync.GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "InstanceChannel.cs");
        Assert.Contains("return new Listener(stop);", source);
        Assert.Contains("public void Dispose() => stop.Cancel();", source);
    }

    [Fact]
    public void Send_WithoutListener_ReturnsFalse()
        => Assert.False(InstanceChannel.Send("eteris-test-nobody-" + Guid.NewGuid().ToString("N"), ["x"], timeoutMs: 300));
}
