using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Infrastructure;

// Processos filhos do motor no rastro do arranque (plano de desempenho 0.2.14, D8: o 1.º renderer está no caminho
// crítico?). Cada filho fica numa linha «filho» com o tempo da CRIAÇÃO dele desde a criação do Éteris — o mesmo
// relógio das marcas —, lido depois da 1.ª pintura (marca aba.apresentada) e de 5 em 5 s até aos 20 s. Nada corre
// antes da 1.ª pintura nem sem ETERIS_TRACE_STARTUP. O 1.º «renderer» é a marca renderer.processo (build/perf/fases.py).
//   <ms da criação do filho>\tfilho\t<tipo>\tpid=..\tvisto=<ms em que foi lido>
// Tipo: o --type= da linha de comandos (renderer, gpu-process, utility:NetworkService…); «?» se o processo não deixar
// ler a linha de comandos (um renderer em sandbox pode recusar).
public static partial class StartupTrace
{
    private static readonly HashSet<int> SeenChildren = [];
    private static int _childrenScheduled;

    private static void AfterMark(string phase)
    {
        if (phase == "aba.apresentada" && Interlocked.Exchange(ref _childrenScheduled, 1) == 0)
            ThreadPool.QueueUserWorkItem(_ => RecordChildren());
    }

    private static void RecordChildren()
    {
        if (FilePath is null) return;
        try
        {
            var lines = new List<string>();
            var now = Elapsed();
            foreach (var (pid, creationUtc, commandLine) in Children(Environment.ProcessId))
            {
                lock (Gate) if (!SeenChildren.Add(pid)) continue;
                // Um PID reutilizado de um processo mais velho do que o Éteris não é filho dele.
                if (creationUtc < _processStartUtc) continue;
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{(creationUtc - _processStartUtc).TotalMilliseconds:F1}\tfilho\t{ChildType(commandLine)}\tpid={pid}\tvisto={now:F0}\n"));
            }
            lock (Gate) foreach (var line in lines) Pending.Append(line);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException) { }
    }

    // O tipo do processo filho do Chromium pela linha de comandos (puro, testado).
    public static string ChildType(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return "?";
        var type = Regex.Match(commandLine, @"--type=([\w-]+)");
        if (!type.Success) return "sem-tipo";
        var sub = Regex.Match(commandLine, @"--utility-sub-type=([\w.]+)");
        return sub.Success ? type.Groups[1].Value + ":" + sub.Groups[1].Value[(sub.Groups[1].Value.LastIndexOf('.') + 1)..] : type.Groups[1].Value;
    }

    private static List<(int Pid, DateTime CreationUtc, string? CommandLine)> Children(int parent)
    {
        var found = new List<(int, DateTime, string?)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return found;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                if (entry.ParentProcessId != (uint)parent) continue;
                var pid = (int)entry.ProcessId;
                var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
                if (process == IntPtr.Zero) continue;
                try
                {
                    if (!GetProcessTimes(process, out var creation, out _, out _, out _)) continue;
                    found.Add((pid, DateTime.FromFileTimeUtc(creation), CommandLineOf(process)));
                }
                finally { CloseHandle(process); }
            }
        }
        finally { CloseHandle(snapshot); }
        return found;
    }

    // Também usada pela vigia dos filhos da rede de segurança (StartupGuard), só depois de uma falha contar.
    internal static string? CommandLineOf(IntPtr process)
    {
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, (uint)size, out var needed);
                if (status == StatusInfoLengthMismatch && needed > size) { size = (int)needed; continue; }
                if (status != 0) return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer } no início do bloco.
                var length = Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length <= 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private const uint SnapProcess = 0x00000002;
    private const uint QueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        // WCHAR szExeFile[MAX_PATH] (não se lê; ushort para a estrutura ser blittable no LibraryImport).
        public fixed ushort ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, uint length, out uint returnLength);
}
