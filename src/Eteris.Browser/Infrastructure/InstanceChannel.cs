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

    public static IDisposable Listen(string name, Action<string[]> onArgs)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName(name), PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token);
                    using var reader = new StreamReader(server);
                    var buffer = new char[MaxMessage];
                    var length = await reader.ReadBlockAsync(buffer, stop.Token);
                    if (JsonSerializer.Deserialize<string[]>(new string(buffer, 0, length)) is { } args) onArgs(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { await Task.Delay(100); }
            }
        });
        return stop;
    }

    public static bool Send(string name, string[] args, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(args));
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
