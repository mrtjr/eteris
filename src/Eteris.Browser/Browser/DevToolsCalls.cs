using CefSharp.Internals;

namespace Eteris.Browser.Browser;

// Todos os pedidos DevTools do Éteris passam por aqui (guarda: DevToolsCallsTests).
//
// Os pedidos de um browser partilham a numeração do CefSharp (IBrowserHost.GetNextDevToolsMessageId), e o CEF só aceita
// números crescentes: um pedido numerado numa thread e executado na do CEF depois de outro mais novo falha («Generated
// MessageId 14 doesn't match returned Message Id 12», medido com o canal do cofre, que pede no commit de cada página ao
// mesmo tempo que os scripts precoces e a miniatura). Aqui, numerar e pôr na fila da thread do CEF acontecem juntos e por
// ordem (a fila é FIFO), e nunca diretamente na thread do CEF: lá o CefSharp executava logo, à frente dos que já estão na
// fila com números mais baixos.
public static class DevToolsCalls
{
    private static readonly Lock s_gate = new();

    public static Task<T> Run<T>(Func<Task<T>> call)
    {
        if (CefThread.CurrentlyOnUiThread) return Task.Run(() => Run(call));
        lock (s_gate) return call();
    }

    public static Task Run(Func<Task> call)
    {
        if (CefThread.CurrentlyOnUiThread) return Task.Run(() => Run(call));
        lock (s_gate) return call();
    }
}
