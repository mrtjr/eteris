using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;

namespace Eteris.Browser.Infrastructure;

// Contadores dos mecanismos para as medições de desempenho (plano 0.2.14, secção 3.3: cada experiência tem um contador
// do mecanismo como prova principal, e a CPU só confirma). SÓ com ETERIS_TEST_CONTADORES (PerfDiagnostics), nunca numa
// instalação. Uma linha por segundo no ficheiro TSV:
//   <ms desde a criação do processo>\tseg\t<nome>=<quantos neste segundo>\t...
// (só os contadores que mexeram nesse segundo; build/perf/contadores.py resume uma janela). Contadores atuais:
//   wpf.lotes            lotes que o WPF enviou ao fio de composição (≈ fotogramas do WPF; 0 em repouso) — E1, D5;
//   camada.mostrar/esconder   a janela em camadas por cima da página aparece/desaparece — E5;
//   midia.sondagem       Runtime.evaluate da central de multimédia, um por aba — E4;
//   pedidos, pedidos.us, pedidos.cancelados   pedidos vistos pelo tratador e tempo gasto nele (µs) — D10;
//   sono.pressao         abas adormecidas por pouca memória (uma por intervalo) — E13.
// Sem a variável, Add é a leitura de um campo estático.
public static class PerfCounters
{
    private static readonly string? FilePath = PerfDiagnostics.CountersPath;
    private static readonly ConcurrentDictionary<string, StrongBox> Counts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Last = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();
    private static readonly StringBuilder Pending = new();
    private static Timer? _timer;
    private static DateTime _processStartUtc;
    private static int _seconds;

    public static bool IsOn => FilePath is not null;

    public static void Add(string name, long value = 1)
    {
        if (FilePath is null) return;
        Interlocked.Add(ref Counts.GetOrAdd(name, _ => new StrongBox()).Value, value);
        EnsureTimer();
    }

    // Um pedido que passou pelo tratador (PrivacyRequestHandler): «started» é o Stopwatch.GetTimestamp() da entrada.
    public static void AddRequest(long started, bool cancelled)
    {
        if (FilePath is null) return;
        Add("pedidos");
        Add("pedidos.us", (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency);
        if (cancelled) Add("pedidos.cancelados");
    }

    // Contador de lotes do WPF do fio da interface de «dispatcher». NÃO usa o CompositionTarget.Rendering: um ouvinte
    // nesse evento obriga o WPF a desenhar todos os fotogramas, e o contador mediria o próprio contador. O evento interno
    // MediaContext.CommittingBatch só dispara quando o WPF já ia enviar um lote. Sem ele (outra versão do WPF), fica
    // registado «wpf.lotes=indisponivel» e nada mais muda.
    public static void WatchWpf(Dispatcher dispatcher)
    {
        if (FilePath is null) return;
        dispatcher.VerifyAccess();
        // Um só ouvinte por fio da interface (todas as janelas desse fio partilham o MediaContext).
        if (_watchingThisThread) return;
        _watchingThisThread = true;
        EnsureTimer();
        try
        {
            var type = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Media.MediaContext", throwOnError: false);
            var context = type?.GetProperty("CurrentMediaContext", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var committing = type?.GetEvent("CommittingBatch", BindingFlags.NonPublic | BindingFlags.Instance);
            var add = committing?.GetAddMethod(nonPublic: true);
            if (context is null || add is null) throw new MissingMemberException("MediaContext.CommittingBatch");
            _wpfBatches = Counts.GetOrAdd("wpf.lotes", _ => new StrongBox());
            var handler = Delegate.CreateDelegate(committing!.EventHandlerType!,
                typeof(PerfCounters).GetMethod(nameof(OnWpfBatch), BindingFlags.NonPublic | BindingFlags.Static)!);
            add.Invoke(context, [handler]);
            Note("wpf.lotes=MediaContext.CommittingBatch");
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMemberException or MemberAccessException or ArgumentException)
        {
            Note("wpf.lotes=indisponivel");
        }
    }

    private static StrongBox? _wpfBatches;
    [ThreadStatic] private static bool _watchingThisThread;

    private static void OnWpfBatch(object? sender, EventArgs e)
    {
        if (_wpfBatches is { } box) Interlocked.Increment(ref box.Value);
    }

    private static void EnsureTimer()
    {
        if (_timer is not null) return;
        lock (Gate)
        {
            if (_timer is not null) return;
            using (var self = Process.GetCurrentProcess()) _processStartUtc = self.StartTime.ToUniversalTime();
            Pending.Append(string.Create(CultureInfo.InvariantCulture,
                $"0\tinfo\tprocesso\tpid={Environment.ProcessId}\tstartUtc={_processStartUtc:O}\tpuro={PerfDiagnostics.PureLayers}\n"));
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { Tick(); Flush(); };
        }
    }

    private static void Note(string text)
    {
        lock (Gate) Pending.Append(string.Create(CultureInfo.InvariantCulture, $"{Elapsed():F0}\tinfo\t{text}\n"));
    }

    private static double Elapsed() => (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;

    private static void Tick()
    {
        var line = new StringBuilder();
        line.Append(Elapsed().ToString("F0", CultureInfo.InvariantCulture)).Append("\tseg");
        lock (Gate)
        {
            foreach (var (name, box) in Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var now = Interlocked.Read(ref box.Value);
                Last.TryGetValue(name, out var before);
                if (now == before) continue;
                Last[name] = now;
                line.Append('\t').Append(name).Append('=').Append((now - before).ToString(CultureInfo.InvariantCulture));
            }
            Pending.Append(line).Append('\n');
        }
        if (Interlocked.Increment(ref _seconds) % 5 == 0) Flush();
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

    private sealed class StrongBox
    {
        public long Value;
    }
}
