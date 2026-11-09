using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Eteris.Browser.Infrastructure;

// Canal entre instâncias: quem chega depois entrega os argumentos (ex.: um link) à instância que já está aberta.
// O pipe só aceita ligações do mesmo utilizador; quem recebe valida os argumentos como num arranque normal.
public static class InstanceChannel
{
    private const int MaxMessage = 16 * 1024;

    private static string PipeName(string name) => name + ".pipe";

    // O primeiro pipe é criado aqui, antes de devolver (menos de 1 ms): quem chega logo a seguir já o encontra. Antes
    // era criado dentro do Task.Run e, com o pool de threads ocupado, um envio feito nesse intervalo esgotava o prazo e
    // perdia o link. Os seguintes são criados no ciclo, depois de cada mensagem. Se o primeiro não se puder criar já
    // (o nome ainda ocupado), o ciclo tenta de novo como antes, sem deitar abaixo o arranque.
    public static IDisposable Listen(string name, Action<string[]> onArgs)
    {
        var stop = new CancellationTokenSource();
        NamedPipeServerStream? first = null;
        try { first = NewServer(name); }
        catch (Exception) { }
        _ = Task.Run(async () =>
        {
            var next = first;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var server = next ?? NewServer(name);
                    next = null;
                    await using (server)
                    {
                        await server.WaitForConnectionAsync(stop.Token);
                        using var reader = new StreamReader(server);
                        var buffer = new char[MaxMessage];
                        var length = await reader.ReadBlockAsync(buffer, stop.Token);
                        if (JsonSerializer.Deserialize<string[]>(new string(buffer, 0, length)) is { } args) onArgs(args);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { await Task.Delay(100); }
            }
            // Parado antes de o ciclo usar o primeiro pipe: fecha-o na mesma.
            if (next is not null) await next.DisposeAsync();
        });
        return stop;
    }

    private static NamedPipeServerStream NewServer(string name) => new(PipeName(name), PipeDirection.In, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static bool Send(string name, string[] args, int timeoutMs = 2000)
    {
        // Dois envios muito seguidos (dois links abertos de outra aplicação) podem apanhar a instância aberta a
        // reabrir o canal para a mensagem seguinte: tenta-se de novo até ao fim do prazo em vez de perder o link.
        var deadline = Environment.TickCount64 + timeoutMs;
        var payload = JsonSerializer.Serialize(args);
        while (true)
        {
            var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(remaining);
                using var writer = new StreamWriter(client);
                writer.Write(payload);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception) when (Environment.TickCount64 + 50 < deadline)
            {
                Thread.Sleep(50);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
