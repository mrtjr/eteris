using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit.Sdk;

namespace Eteris.Browser.E2E.Tests;

// O Éteris compilado, a correr num perfil temporário (--user-data-dir), com o DevTools do motor numa porta livre em
// 127.0.0.1 (ETERIS_TEST_DEVTOOLS_PORT) e sem som (ETERIS_TEST_MUTE). Nunca o perfil do dono nem a versão instalada.
public sealed class EterisProcess : IAsyncDisposable
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Process _process;
    private readonly DirectoryInfo _profile;
    private readonly int _port;

    private EterisProcess(Process process, DirectoryInfo profile, int port)
    {
        _process = process;
        _profile = profile;
        _port = port;
    }

    public bool HasExited => _process.HasExited;

    // ETERIS_E2E_EXE, se existir (por exemplo, publish\Eteris.Browser.exe); senão o executável compilado na mesma
    // configuração deste projeto de teste.
    public static string ExePath()
    {
        if (Environment.GetEnvironmentVariable("ETERIS_E2E_EXE") is { Length: > 0 } custom) return Path.GetFullPath(custom);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var exe = Path.Combine(Root(), "src", "Eteris.Browser", "bin", configuration, "net10.0-windows", "win-x64", "Eteris.Browser.exe");
        if (!File.Exists(exe))
            throw new XunitException($"compile primeiro: dotnet build src/Eteris.Browser -c {configuration} (falta {Path.GetRelativePath(Root(), exe)})");
        return exe;
    }

    public static Task<EterisProcess> StartAsync(string url, params string[] arguments)
        => StartAsync(url, new Dictionary<string, string>(), arguments);

    // Com variáveis ETERIS_TEST_* próprias do teste (por cima das de omissão: porta do DevTools e som cortado).
    public static Task<EterisProcess> StartAsync(string url, IReadOnlyDictionary<string, string> env, params string[] arguments)
    {
        var exe = ExePath();
        var profile = Directory.CreateTempSubdirectory("eteris-e2e-");
        var port = FreePort();
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        // As ETERIS_* da consola de quem corre não chegam ao processo; só as do teste.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("ETERIS_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        start.Environment["ETERIS_TEST_DEVTOOLS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ETERIS_TEST_MUTE"] = "1";
        foreach (var (name, value) in env) start.Environment[name] = value;
        start.ArgumentList.Add("--user-data-dir=" + profile.FullName);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(url);
        var process = Process.Start(start) ?? throw new XunitException("o Éteris não arrancou: " + exe);
        return Task.FromResult(new EterisProcess(process, profile, port));
    }

    // O primeiro separador do motor («page») cujo endereço cumpre «url», ligado por WebSocket.
    public async Task<CdpPage> PageAsync(Func<string, bool> url, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        while (deadline.Elapsed < timeout)
        {
            if (_process.HasExited) throw new XunitException($"o processo saiu com o código {_process.ExitCode}");
            try
            {
                var json = await s_http.GetStringAsync($"http://127.0.0.1:{_port}/json/list");
                using var targets = JsonDocument.Parse(json);
                foreach (var target in targets.RootElement.EnumerateArray())
                {
                    if (target.GetProperty("type").GetString() != "page") continue;
                    var address = target.GetProperty("url").GetString() ?? "";
                    seen.Add(address);
                    if (url(address) && target.TryGetProperty("webSocketDebuggerUrl", out var ws) && ws.GetString() is { } socket)
                        return await CdpPage.ConnectAsync(new Uri(socket));
                }
            }
            catch (HttpRequestException) { }      // a porta ainda não abriu: «ainda não»
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        if (_process.HasExited) throw new XunitException($"o processo saiu com o código {_process.ExitCode}");
        throw new XunitException($"nenhuma página serviu em {timeout.TotalSeconds:0} s (DevTools em 127.0.0.1:{_port}); vistas: " + (seen.Count == 0 ? "nenhuma" : string.Join(", ", seen)));
    }

    // Minimiza a janela principal deste processo (só deste: nunca a de outro Éteris aberto), à espera que ela exista.
    public void MinimizeMainWindow()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            _process.Refresh();
            if (_process.HasExited) throw new XunitException($"o processo saiu com o código {_process.ExitCode}");
            if (_process.MainWindowHandle != 0)
            {
                ShowWindow(_process.MainWindowHandle, SwMinimize);
                return;
            }
            Thread.Sleep(250);
        }
        throw new XunitException("a janela principal não apareceu em 30 s");
    }

    private const int SwMinimize = 6;

    // Fecha a janela principal deste processo (como o «X») e diz se o processo saiu dentro do prazo.
    public async Task<bool> CloseMainWindowAsync(TimeSpan timeout)
    {
        _process.Refresh();
        if (_process.HasExited) return true;
        _process.CloseMainWindow();
        using var wait = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(wait.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _process.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { } // o Kill apanhou o processo já a sair
        finally { _process.Dispose(); }

        // O motor solta o perfil um pouco depois de o processo principal sair; se não der, a pasta fica.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (_profile.Exists) _profile.Delete(recursive: true); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(500);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    internal static string Root([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
