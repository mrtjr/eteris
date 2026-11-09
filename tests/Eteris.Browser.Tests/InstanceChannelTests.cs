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
        // Muitos envios seguidos: no servidor do GitHub (mais lento) um deles falhava ao apanhar o canal a reabrir.
        using var listener = InstanceChannel.Listen(name, _ => { if (Interlocked.Increment(ref count) == 10) done.TrySetResult(); });
        for (var i = 0; i < 10; i++) Assert.True(InstanceChannel.Send(name, [$"https://s{i}.pt/"]), $"envio {i} falhou");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Fact]
    public void Send_WithoutListener_ReturnsFalse()
        => Assert.False(InstanceChannel.Send("eteris-test-nobody-" + Guid.NewGuid().ToString("N"), ["x"], timeoutMs: 300));
}
