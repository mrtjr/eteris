using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Eteris.Browser.Infrastructure;

// Medição do arranque e da memória, SÓ com a variável de ambiente ETERIS_TRACE_STARTUP (para os testes de desempenho;
// numa instalação só com --user-data-dir, ver ResolveFor):
// - «1»: escreve em %TEMP%\eteris-trace-<pid>.tsv; outro valor é o caminho do ficheiro ({pid} é trocado pelo processo).
// - Cada marca (Mark) regista o tempo desde a criação do processo, a thread, a memória privada (commit) e o trabalho do
//   JIT até ali (métodos compilados e tempo de compilação acumulado).
// - Fotografias da memória (heap .NET por geração, heaps nativos do Windows, threads, assemblies) a cada 5 s durante
//   2 min; ETERIS_TRACE_GC_AT=20,60 (só conta com o rastro ligado) força um GC completo (compactado) antes da fotografia desses segundos.
// - Endereço de um objeto do heap .NET (gcprobe), para uma ferramenta externa saber que reserva do processo é o GC.
// Sem a variável, Mark e Snapshot não fazem nada (uma leitura de campo estático).
public static partial class StartupTrace
{
    // Só olha para o disco quando a variável existe. O AppPaths.UseRoot ainda não correu quando a 1.ª marca se escreve:
    // o perfil de teste lê-se dos argumentos (--user-data-dir).
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("ETERIS_TRACE_STARTUP") is { Length: > 0 } value
        ? ResolveFor(value, Environment.ProcessId, TestSwitches.IsInstalled, StartupArgs.UserDataDir(Environment.GetCommandLineArgs()) is not null)
        : null;
    private static readonly Lock Gate = new();
    private static readonly StringBuilder Pending = new();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly HashSet<int> GcAt = ParseSeconds(Environment.GetEnvironmentVariable("ETERIS_TRACE_GC_AT"));
    private static DateTime _processStartUtc;
    private static Timer? _timer;
    private static int _ticks;
    private static byte[]? _probe;

    public static bool IsOn => FilePath is not null;

    // Numa instalação, só com um perfil de teste (--user-data-dir): com o perfil normal, uma variável esquecida no ambiente
    // não faz o Éteris instalado escrever o rastro (INF-11).
    public static string? ResolveFor(string? value, int processId, bool installed, bool customProfile)
        => installed && !customProfile ? null : Resolve(value, processId);

    public static string? Resolve(string? value, int processId) => value?.Trim() switch
    {
        null or "" or "0" => null,
        "1" => Path.Combine(Path.GetTempPath(), $"eteris-trace-{processId}.tsv"),
        var path => path.Replace("{pid}", processId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
    };

    public static HashSet<int> ParseSeconds(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n > 0).ToHashSet();

    // Uma marca de fase. «detail»: texto livre (tamanhos, contagens).
    public static void Mark(string phase, string? detail = null)
    {
        if (FilePath is null) return;
        Write("mark", phase, detail);
        AfterMark(phase);
    }

    // Só a 1.ª vez que a fase acontece (a 1.ª aba, a 1.ª pintura).
    public static void MarkOnce(string phase, string? detail = null)
    {
        if (FilePath is null) return;
        lock (Gate) if (!Seen.Add(phase)) return;
        Write("mark", phase, detail);
    }

    private static void Write(string kind, string phase, string? detail)
    {
        var ms = Elapsed();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{ms:F1}\t{kind}\t{phase}\tthread={Environment.CurrentManagedThreadId}\tpriv={PrivateMb():F1}\tjit={System.Runtime.JitInfo.GetCompiledMethodCount()}\tjitms={System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:F0}\talloc={GC.GetTotalAllocatedBytes() / 1048576.0:F1}\t{detail}\n");
        lock (Gate)
        {
            Pending.Append(line);
            if (_timer is null)
            {
                _timer = new Timer(_ => Tick(), null, 5000, 5000);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
            }
        }
    }

    private static double Elapsed()
    {
        if (_processStartUtc == default)
        {
            using var self = Process.GetCurrentProcess();
            _processStartUtc = self.StartTime.ToUniversalTime();
            if (FilePath is not null)
            {
                _probe = GC.AllocateArray<byte>(16, pinned: true);
                lock (Gate)
                    Pending.Append(string.Create(CultureInfo.InvariantCulture,
                        $"0\tinfo\tprocesso\tpid={Environment.ProcessId}\tstartUtc={_processStartUtc:O}\tgcprobe=0x{Marshal.UnsafeAddrOfPinnedArrayElement(_probe, 0):X}\tserverGC={GCSettings.IsServerGC}\tconcurrent={GCSettings.LatencyMode}\tcores={Environment.ProcessorCount}\n"));
            }
        }
        return (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
    }

    private static void Tick()
    {
        var n = Interlocked.Increment(ref _ticks);
        var seconds = n * 5;
        if (GcAt.Contains(seconds))
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Snapshot($"gc{seconds}s");
        }
        else Snapshot($"t{seconds}s");
        // Filhos que nasceram depois da 1.ª pintura (o renderer de reserva, o serviço de áudio…): StartupTrace.Children.cs.
        if (n <= 4) RecordChildren();
        if (n >= 24) _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Flush();
    }

    // Uma criação medida (as tabelas da base de dados abrem-se assim, à 1.ª utilização); sem registo, a própria função.
    public static Func<T> Timed<T>(string name, Func<T> make)
    {
        if (FilePath is null) return make;
        return () =>
        {
            var clock = Stopwatch.StartNew();
            var value = make();
            Mark("bd." + name, string.Create(CultureInfo.InvariantCulture, $"ms={clock.Elapsed.TotalMilliseconds:F1}"));
            return value;
        };
    }

    // Um Lazy cuja criação fica medida.
    public static Lazy<T> Lazy<T>(string name, Func<T> make) => new(Timed(name, make));

    // Fotografia da memória do processo.
    public static void Snapshot(string label)
    {
        if (FilePath is null) return;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var gens = new StringBuilder();
        string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
        for (var i = 0; i < Math.Min(names.Length, info.GenerationInfo.Length); i++)
            gens.Append(CultureInfo.InvariantCulture, $"{names[i]}={info.GenerationInfo[i].SizeAfterBytes / 1048576.0:F1}/{info.GenerationInfo[i].FragmentationAfterBytes / 1048576.0:F1}\t");
        var (heaps, heapCommitted, heapAllocated) = NativeHeaps();
        using var self = Process.GetCurrentProcess();
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"gcIndex={info.Index}\tgcHeap={info.HeapSizeBytes / 1048576.0:F1}\tgcCommitted={info.TotalCommittedBytes / 1048576.0:F1}\tgcFrag={info.FragmentedBytes / 1048576.0:F1}\tgcTotalNow={GC.GetTotalMemory(false) / 1048576.0:F1}\tgcCount={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}\t{gens}ntHeaps={heaps}\tntHeapCommitted={heapCommitted / 1048576.0:F1}\tntHeapAllocated={heapAllocated / 1048576.0:F1}\tws={self.WorkingSet64 / 1048576.0:F1}\tthreads={self.Threads.Count}\thandles={self.HandleCount}\tassemblies={AppDomain.CurrentDomain.GetAssemblies().Length}\tjitIL={System.Runtime.JitInfo.GetCompiledILBytes() / 1024.0:F0}KB");
        Write("mem", label, detail);
    }

    public static void Flush()
    {
        if (FilePath is null) return;
        string text;
        lock (Gate)
        {
            if (Pending.Length == 0) return;
            text = Pending.ToString();
            Pending.Clear();
        }
        try { File.AppendAllText(FilePath, text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static double PrivateMb()
    {
        var counters = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? counters.PrivateUsage / 1048576.0 : -1;
    }

    private static (int Count, long Committed, long Allocated) NativeHeaps()
    {
        var handles = new IntPtr[256];
        var count = (int)GetProcessHeaps((uint)handles.Length, handles);
        long committed = 0, allocated = 0;
        for (var i = 0; i < Math.Min(count, handles.Length); i++)
        {
            var summary = new HeapSummaryInfo { Cb = (uint)Marshal.SizeOf<HeapSummaryInfo>() };
            if (HeapSummary(handles[i], 0, ref summary))
            {
                committed += (long)summary.Committed;
                allocated += (long)summary.Allocated;
            }
        }
        return (count, committed, allocated);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryInfo
    {
        public uint Cb;
        public nuint Allocated;
        public nuint Committed;
        public nuint Reserved;
        public nuint MaxReserve;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetProcessHeaps(uint count, [Out] IntPtr[] heaps);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryInfo summary);
}
