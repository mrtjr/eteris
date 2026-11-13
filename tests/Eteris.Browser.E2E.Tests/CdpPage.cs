using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace Eteris.Browser.E2E.Tests;

// Uma página do Éteris pelo protocolo do DevTools (CDP): Runtime.evaluate e Page.navigate. Sem cabeçalho Origin, como o
// build/perf/rastro.ps1: o motor só aceita ligações sem origem ou de origens autorizadas.
public sealed class CdpPage : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private int _id;

    private CdpPage(ClientWebSocket socket) => _socket = socket;

    internal static async Task<CdpPage> ConnectAsync(Uri url)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(url, timeout.Token);
        return new CdpPage(socket);
    }

    // Leva a página a outro endereço (Page.navigate) e não espera pela carga: quem chama espera com WaitForAsync.
    public async Task NavigateAsync(string url)
    {
        using var message = await CallAsync("Page.navigate", new { url }, url);
        if (message.RootElement.GetProperty("result").TryGetProperty("errorText", out var error))
            throw new XunitException($"o motor não abriu «{url}»: {error.GetString()}");
    }

    public async Task<T> EvaluateAsync<T>(string expression)
    {
        using var message = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, expression);
        var result = message.RootElement.GetProperty("result");
        if (result.TryGetProperty("exceptionDetails", out var exception))
            throw new XunitException($"«{expression}» lançou: {exception.GetRawText()}");
        var value = result.GetProperty("result");
        if (!value.TryGetProperty("value", out var raw))
            throw new XunitException($"«{expression}» não devolveu um valor: {value.GetRawText()}");
        return raw.Deserialize<T>()!;
    }

    // Um pedido e a resposta com o mesmo id: os eventos (sem id) e as respostas a outros pedidos ficam de fora.
    private async Task<JsonDocument> CallAsync(string method, object parameters, string what)
    {
        var id = Interlocked.Increment(ref _id);
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
        while (true)
        {
            var message = JsonDocument.Parse(await ReceiveAsync(timeout.Token));
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var got) || got.GetInt32() != id)
            {
                message.Dispose();
                continue;
            }
            if (root.TryGetProperty("error", out var error))
            {
                var text = error.GetRawText();
                message.Dispose();
                throw new XunitException($"o CDP recusou «{what}»: {text}");
            }
            return message;
        }
    }

    // Avalia «expression» de 250 em 250 ms até dar true. No fim do prazo falha com a expressão e, com «describe», com o
    // valor dele (o que a página mostra).
    public async Task WaitForAsync(string expression, TimeSpan timeout, string? describe = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (await EvaluateAsync<bool>("!!(" + expression + ")")) return;
            await Task.Delay(250);
        }
        var shown = describe is null ? "" : "; " + describe + " = " + await EvaluateAsync<JsonElement>(describe);
        throw new XunitException($"«{expression}» não ficou verdadeira em {timeout.TotalSeconds:0} s{shown}");
    }

    private async Task<string> ReceiveAsync(CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        using var all = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close) throw new XunitException("o motor fechou a ligação do DevTools");
            all.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(all.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally { _socket.Dispose(); }
    }
}
