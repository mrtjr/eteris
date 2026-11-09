using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O motor de composição do WPF arranca numa thread à parte e essa thread termina depois da 1.ª página. Arranca e para o
// motor de propósito: corre sozinho, com os outros testes de janelas WPF.
[Collection(WpfCollection.Name)]
public class CompositionWarmupTests
{
    [Fact]
    public async Task Start_CreatesItsOwnDispatcherAndStopEndsIt()
    {
        CompositionWarmup.Start();
        for (var i = 0; i < 500 && CompositionWarmup.Current is null; i++) await Task.Delay(10);
        var dispatcher = CompositionWarmup.Current;
        Assert.NotNull(dispatcher);
        Assert.NotEqual(Environment.CurrentManagedThreadId, dispatcher.Thread.ManagedThreadId);
        CompositionWarmup.Stop();
        Assert.True(dispatcher.Thread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(dispatcher.HasShutdownFinished);
    }
}
