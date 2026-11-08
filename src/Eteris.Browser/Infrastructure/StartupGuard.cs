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
// Sem sandbox possível (publicação normal, CefSharp oficial) não faz nada: nem ficheiro, nem vigia.
public sealed class StartupGuard(string path, Func<TimeSpan> sinceStart)
{
    private enum Outcome { Pending, Healthy, Failed }

    private readonly Lock _gate = new();
    private SandboxFallbackState? _begun;
    private SandboxFallbackState? _toWrite;
    private Outcome _outcome;
    private int _childFailures;

    public static StartupGuard Current { get; private set; } = new(string.Empty, () => TimeSpan.Zero);

    // O aviso da nova aba neste arranque (SandboxFallback.NoticeText).
    public SandboxNotice Notice { get; private set; }

    public bool Active { get { lock (_gate) return _begun is not null; } }

    public static StartupGuard ForProfile(string root)
    {
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return Current = new StartupGuard(Path.Combine(root, SandboxFallback.FileName), () => DateTime.UtcNow - started);
    }

    public SandboxStart Begin(string version, bool available, bool manualOff, bool retried)
    {
        if (!available) return new SandboxStart(new SandboxFallbackState(), false, SandboxNotice.None);
        var start = SandboxFallback.Begin(Read(path), version, available, manualOff, retried);
        lock (_gate)
        {
            _begun = start.State;
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
    public void ChildFailed(uint exitCode, TimeSpan? at = null)
    {
        if (!SandboxFallback.IsChildFailure(exitCode) || !SandboxFallback.InWindow(at ?? sinceStart())) return;
        CountChildFailure();
    }

    // Uma página terminou (CefSharp OnRenderProcessTerminated): só as que o motor nem conseguiu lançar contam aqui (as que
    // caíram deixam um código de saída que a vigia dos filhos vê).
    public void RenderProcessTerminated(int terminationStatus)
    {
        if (!SandboxFallback.IsLaunchFailure(terminationStatus) || !SandboxFallback.InWindow(sinceStart())) return;
        CountChildFailure();
    }

    private void CountChildFailure()
    {
        lock (_gate)
        {
            if (_begun is not { WithSandbox: true } begun || _outcome == Outcome.Failed) return;
            if (++_childFailures < SandboxFallback.ChildFailuresToCount) return;
            // Falhado, mesmo que a 1.ª página já tenha aparecido (a contagem de antes deste arranque é a de _begun).
            _outcome = Outcome.Failed;
            _toWrite = SandboxFallback.Failed(begun);
        }
        _ = Task.Run(Flush);
    }

    public int ChildFailures { get { lock (_gate) return _childFailures; } }

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
        try
        {
            while (true)
            {
                var inWindow = SandboxFallback.InWindow(sinceStart());
                if (inWindow)
                    foreach (var pid in ChildProcesses(self, exeName))
                        if (!watched.ContainsKey(pid))
                            watched[pid] = OpenProcess(Synchronize | QueryLimitedInformation, false, pid);
                foreach (var (pid, handle) in watched.ToList())
                {
                    if (handle == 0 || WaitForSingleObject(handle, 0) != 0) continue;
                    if (GetExitCodeProcess(handle, out var code)) ChildFailed(code);
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
