using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Eteris.Browser.Infrastructure;

// O lado com ficheiros e processos da rede de segurança do sandbox (a lógica está em SandboxFallback):
//   - Begin (CefConfigurator.Prepare, antes do Cef.Initialize): lê o estado do perfil, decide se este arranque usa o
//     sandbox e grava o marcador «a arrancar» (síncrono: tem de estar no disco antes de o motor arrancar).
//   - Healthy: a 1.ª página apareceu (MainWindow.PageVisibility) ou o Éteris fechou normalmente (App.OnExit).
//   - ChildFailed / RenderProcessTerminated: um filho morreu com um código de falha no início do arranque (vigia dos
//     filhos, em baixo) ou o motor não conseguiu lançar uma página; ChildFailuresToCount fazem do arranque um falhado.
//   - De onde veio cada falha contada fica no próprio estado (SandboxFallbackState.Origins): a queda vista pelo Begin do
//     arranque seguinte, ou o tipo e o código de saída de cada filho. Só se escreve quando uma falha conta (regra R-2
//     do plano do sandbox: nada de novo num arranque normal).
//   - SuspendTweaksAfterAFailedStart (início do Program.Main): o 1.º degrau, os ajustes de desempenho desligados depois de
//     um arranque falhado com eles (D-18; SandboxFallback.TweaksSuspended).
// Sem sandbox possível (publicação normal, CefSharp oficial) não faz nada: nem ficheiro, nem vigia.
public sealed class StartupGuard(string path, Func<TimeSpan> sinceStart, Func<DateTime>? utcNow = null)
{
    private enum Outcome { Pending, Healthy, Failed }

    private readonly Lock _gate = new();
    private SandboxFallbackState? _begun;
    private SandboxFallbackState? _toWrite;
    private Outcome _outcome;
    private int _childFailures;
    // Já houve conteúdo web confirmado neste arranque (WebContentCommitted): as quedas dos filhos deixam de contar.
    private bool _webContent;
    private string? _version;
    // De onde veio cada falha de filho deste arranque (SandboxFallback.ChildOrigin); vai para o estado se contarem.
    private readonly List<string> _childOrigins = [];
    // A hora das linhas de Origins: só se lê quando uma falha conta.
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public static StartupGuard Current { get; private set; } = new(string.Empty, () => TimeSpan.Zero);

    // O aviso da nova aba neste arranque (SandboxFallback.NoticeText).
    public SandboxNotice Notice { get; private set; }

    public bool Active { get { lock (_gate) return _begun is not null; } }

    public static StartupGuard ForProfile(string root)
    {
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return Current = new StartupGuard(Path.Combine(root, SandboxFallback.FileName), () => DateTime.UtcNow - started);
    }

    // O arranque anterior falhou com o sandbox ainda ligado (SandboxFallback.PreviousStartFailed): a sessão volta com as
    // abas todas a dormir (MainWindow.StartTabs). Lido depois do Begin.
    public bool PreviousStartFailed { get; private set; }

    public SandboxStart Begin(string version, bool available, bool manualOff, bool retried, bool tweaks = false)
    {
        if (!available) return new SandboxStart(new SandboxFallbackState(), false, SandboxNotice.None);
        var previous = Read(path);
        PreviousStartFailed = SandboxFallback.PreviousStartFailed(previous);
        var start = SandboxFallback.Begin(previous, version, available, manualOff, retried, tweaks, _utcNow);
        lock (_gate)
        {
            _begun = start.State;
            _version = version;
            Notice = start.Notice;
        }
        Write(path, start.State);
        return start;
    }

    // A 1.ª página apareceu, ou fecho normal. Grava fora do fio da interface (Flush, no fecho, grava logo).
    public void Healthy()
    {
        lock (_gate)
        {
            if (_begun is null || _outcome != Outcome.Pending) return;
            _outcome = Outcome.Healthy;
            _toWrite = SandboxFallback.Healthy(_begun);
        }
        _ = Task.Run(Flush);
    }

    // Um processo filho saiu com este código. Só conta com o sandbox ligado, no início do arranque e com um código de falha.
    // «type»: o tipo do filho (SandboxFallback.ChildKind), quando a vigia o chegou a ler.
    public void ChildFailed(uint exitCode, TimeSpan? at = null, string? type = null)
    {
        if (!SandboxFallback.IsChildFailure(exitCode)) return;
        var when = at ?? sinceStart();
        if (!SandboxFallback.InWindow(when)) return;
        CountChildFailure(SandboxFallback.ChildOrigin(type, exitCode, when));
    }

    // Uma página terminou (CefSharp OnRenderProcessTerminated): só as que o motor nem conseguiu lançar contam aqui (as que
    // caíram deixam um código de saída que a vigia dos filhos vê).
    public void RenderProcessTerminated(int terminationStatus)
    {
        if (!SandboxFallback.IsLaunchFailure(terminationStatus)) return;
        var when = sinceStart();
        if (!SandboxFallback.InWindow(when)) return;
        CountChildFailure(SandboxFallback.LaunchOrigin(terminationStatus, when));
    }

    // Uma moldura principal (aba, painel web ou Espreitar) confirmou conteúdo web (SandboxFallback.IsWebContent): daí em
    // diante as quedas dos filhos já não contam, porque a página as pode provocar (INF-01). O corte não pode ser o
    // Healthy: é a página que decide quando ele acontece (MainWindow.PageVisibility).
    public void WebContentCommitted()
    {
        lock (_gate) _webContent = true;
    }

    private void CountChildFailure(string origin)
    {
        lock (_gate)
        {
            if (_webContent || _begun is not { WithSandbox: true } begun || _outcome == Outcome.Failed) return;
            _childOrigins.Add(origin);
            if (++_childFailures < SandboxFallback.ChildFailuresToCount) return;
            // Falhado, mesmo que a 1.ª página interna já tenha aparecido (a contagem de antes deste arranque é a de _begun).
            _outcome = Outcome.Failed;
            _toWrite = SandboxFallback.Failed(begun, _version, SandboxFallback.ChildrenOrigin + " " + string.Join(' ', _childOrigins), _utcNow);
        }
        _ = Task.Run(Flush);
    }

    public int ChildFailures { get { lock (_gate) return _childFailures; } }

    public const string TweaksSuspendedReason = "rede de segurança: um arranque anterior desta versão falhou com eles";

    // O 1.º degrau da rede de segurança (D-18), no início do Program.Main, antes da 1.ª leitura de um ajuste (o e10 lê-se
    // logo a seguir, antes do Velopack.Run). Só lê o disco quando há ajustes antes da 1.ª página ligados e o sandbox é
    // possível (na configuração de omissão, nenhum: nada de novo antes da 1.ª página, regra R-2 do sandbox). O perfil é o
    // do --user-data-dir dos argumentos, como o AppPaths.UseRoot faz a seguir.
    public static bool SuspendTweaksAfterAFailedStart(IReadOnlyList<string> args)
    {
        if (!PerfTuning.AnyBeforeFirstPaint || !CefConfigurator.SandboxSupported || SandboxInfo.Available == 0) return false;
        var root = StartupArgs.UserDataDir(args) is { } dir ? Path.GetFullPath(dir) : AppPaths.DefaultRoot;
        if (!SandboxFallback.TweaksSuspended(Read(Path.Combine(root, SandboxFallback.FileName)), Browser.SettingsPage.Version)) return false;
        PerfTuning.Suspend(TweaksSuspendedReason);
        return true;
    }

    // Grava o que estiver por gravar (no fecho: antes de o processo sair).
    public void Flush()
    {
        lock (_gate)
        {
            if (_toWrite is not { } state) return;
            _toWrite = null;
            Write(path, state);
        }
    }

    internal static SandboxFallbackState Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SandboxFallbackState>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    // Ficheiro temporário e troca: um corte a meio nunca deixa um estado partido.
    internal static void Write(string file, SandboxFallbackState state)
    {
        try
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Vigia dos filhos (só com sandbox, só durante SandboxFallback.ChildWindow): de 250 em 250 ms procura os processos
    // filhos deste com o mesmo executável (o bootstrap: páginas, placa gráfica, utilitários — não os do WebView2 nem
    // outros programas) e, quando um sai, entrega o código de saída a ChildFailed. Uma fotografia de processos custa ~1 ms
    // numa thread de fundo; no fim fecha tudo.
    // O tipo de cada filho (o --type= da linha de comandos, para o registo de onde veio a falha) só se lê depois de a 1.ª
    // falha ter contado (num arranque normal a vigia não faz nada de novo: regra R-2) e enquanto o filho está vivo
    // (depois de sair, o Windows já não dá a linha de comandos): o 1.º filho a falhar fica com «?», os seguintes (os que
    // já estavam vivos e os que o motor volta a lançar) com o tipo. A linha de comandos nunca é guardada.
    public void WatchChildren()
    {
        lock (_gate)
            if (_begun is not { WithSandbox: true }) return;
        var thread = new Thread(WatchLoop) { IsBackground = true, Name = "Éteris: vigia dos filhos no arranque", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private void WatchLoop()
    {
        var self = (uint)Environment.ProcessId;
        var exeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        var watched = new Dictionary<uint, nint>();
        Dictionary<uint, string>? types = null;
        try
        {
            while (true)
            {
                var inWindow = SandboxFallback.InWindow(sinceStart());
                if (inWindow)
                    foreach (var pid in ChildProcesses(self, exeName))
                        if (!watched.ContainsKey(pid))
                            watched[pid] = OpenProcess(Synchronize | QueryLimitedInformation, false, pid);
                if (types is null && ChildFailures > 0) types = [];
                if (types is not null)
                    foreach (var (pid, handle) in watched)
                        if (handle != 0 && !types.ContainsKey(pid))
                            types[pid] = SandboxFallback.ChildKind(StartupTrace.CommandLineOf(handle));
                foreach (var (pid, handle) in watched.ToList())
                {
                    if (handle == 0 || WaitForSingleObject(handle, 0) != 0) continue;
                    if (GetExitCodeProcess(handle, out var code)) ChildFailed(code, type: types?.GetValueOrDefault(pid));
                    CloseHandle(handle);
                    watched[pid] = 0;
                }
                if (!inWindow) return;
                Thread.Sleep(250);
            }
        }
        catch (Exception)
        {
            // A vigia nunca pode derrubar o Éteris.
        }
        finally
        {
            foreach (var handle in watched.Values)
                if (handle != 0) CloseHandle(handle);
        }
    }

    private static List<uint> ChildProcesses(uint parent, string exeName)
    {
        var found = new List<uint>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1) return found;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                if (entry.ParentProcessId == parent && string.Equals(entry.ExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                    found.Add(entry.ProcessId);
        }
        finally { CloseHandle(snapshot); }
        return found;
    }

    private const uint SnapProcess = 0x2;
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
