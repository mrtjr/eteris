using System.Globalization;
using System.Runtime.InteropServices;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tabs;

// E13 do plano de desempenho da 0.2.14 (ajuste e13, ligado por omissão pela decisão D-6: o modo padrão é o equilibrado
// e entra já com a rolagem restaurada; a opção «Adormecer também quando falta memória» das Definições desliga-o). O sono
// por tempo (SleepPolicy) não reage com a memória apertada; com o ajuste, junta-se-lhe o sinal de pouca memória, como o
// Tab Unloading do Firefox: o aviso do Windows (LowMemoryResourceNotification) ou o commit livre quase esgotado (as falhas
// por falta de commit acontecem mesmo com RAM livre). Com pressão adormece UMA aba por intervalo, a usada há mais tempo,
// com as isenções do sono por tempo e mais as fixadas, a captura (WebRTC) e a multimédia a tocar sem som.
//
// Modos, com a escolha de tempo que já existe nas Definições (SleepMinutes):
//   moderado     60 min + pressão;
//   equilibrado  20 min + pressão (o padrão; sem pressão faz o mesmo que hoje);
//   máximo       5 min + pressão;
//   só tempo     SleepOnLowMemory desligado (o recuo do plano);
//   «Nunca»      nem por tempo nem por pressão.
// Regra pura e o sinal do Windows; o temporizador em MainWindow.Pressure.cs.
public static class MemoryPressure
{
    // Um falso positivo custa no máximo uma aba por intervalo. Com a pressão mantida, só adormecem abas deixadas há
    // pelo menos MinIdle: duas abas usadas à vez já não se adormecem uma à outra (cada troca recarregava a página).
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MinIdle = TimeSpan.FromMinutes(2);

    // Commit livre abaixo disto (ou de 5% do total) conta como pressão.
    public const ulong LowCommitBytes = 512UL << 20;
    public const double LowCommitShare = 0.05;

    public static bool IsLow(bool lowMemorySignal, ulong availableCommit, ulong totalCommit)
        => lowMemorySignal || (totalCommit > 0 && (availableCommit < LowCommitBytes || availableCommit < totalCommit * LowCommitShare));

    public static bool Applies(UserSettings settings) => settings.SleepMinutes > 0 && settings.SleepOnLowMemory;

    public static string ModeName(int sleepMinutes) => sleepMinutes switch
    {
        0 => "nunca",
        5 => "máximo",
        20 => "equilibrado",
        60 => "moderado",
        _ => "personalizado",
    };

    // Held = a página pediu há pouco para ficar acordada (texto por enviar, Picture-in-Picture; SleepProbe).
    public sealed record Candidate<T>(T Tab, bool OnScreen, bool Loading, bool Audible, bool Locked, bool Pinned, bool Capturing,
        bool Playing, DateTime LastActiveUtc, bool Held = false);

    public static bool MaySleep<T>(Candidate<T> c)
        => !c.OnScreen && !c.Loading && !c.Audible && !c.Locked && !c.Pinned && !c.Capturing && !c.Playing && !c.Held;

    // A aba acordada usada há mais tempo, deixada há pelo menos MinIdle, que pode adormecer; nenhuma, se todas estão
    // isentas ou foram usadas há pouco.
    public static T? Pick<T>(IEnumerable<Candidate<T>> candidates, DateTime nowUtc) where T : class
        => candidates.Where(c => MaySleep(c) && nowUtc - c.LastActiveUtc >= MinIdle).OrderBy(c => c.LastActiveUtc).FirstOrDefault()?.Tab;

    // ---------- O sinal do Windows ----------

    private static readonly Lazy<IntPtr> LowMemoryHandle = new(() =>
    {
        try { return CreateMemoryResourceNotification(LowMemoryResourceNotification); }
        catch (Exception) { return IntPtr.Zero; }
    });

    // Pressão agora: o sinal simulado das medições (ETERIS_TEST_PRESSAO, inerte numa instalação) ou o do Windows.
    public static bool IsLowNow()
    {
        if (Infrastructure.PerfDiagnostics.SimulatedPressure) return true;
        try
        {
            var handle = LowMemoryHandle.Value;
            var signal = handle != IntPtr.Zero && QueryMemoryResourceNotification(handle, out var low) && low;
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? IsLow(signal, status.AvailPageFile, status.TotalPageFile)
                : signal;
        }
        catch (Exception) { return false; }
    }

    private const int LowMemoryResourceNotification = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool state);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

// A rolagem da página antes de adormecer (ajuste e13), reposta quando a mesma página volta a carregar ao acordar. Só em
// memória, nunca no disco (o session.json não muda).
public sealed record SleepScroll(string Url, double Y)
{
    // O que o PageScript devolveu (um número) para a página que vai adormecer; null sem rolagem ou sem resposta.
    public static SleepScroll? From(string url, object? result)
        => result is double y && double.IsFinite(y) && y > 0 ? new SleepScroll(url, y) : null;

    public const string ReadScript = "(function(){return window.scrollY||0;})()";

    public bool Matches(string url) => string.Equals(Url, url, StringComparison.Ordinal);

    // Depois do 1.º desenho, e só se a pessoa ainda não rolou.
    public string RestoreScript
        => "requestAnimationFrame(function(){if(window.scrollY===0)window.scrollTo(0,"
           + Y.ToString("0.##", CultureInfo.InvariantCulture) + ");})";
}
