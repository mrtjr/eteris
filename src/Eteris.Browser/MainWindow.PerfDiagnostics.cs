using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// Modos de diagnóstico das medições de desempenho (Infrastructure/PerfDiagnostics.cs, build/perf/README.md). Só com as
// variáveis ETERIS_TEST_*; numa instalação não fazem nada.
public partial class MainWindow
{
    private void ApplyPerfDiagnostics()
    {
        // Contador de lotes do WPF (≈ fotogramas por segundo do processo principal), a prova do E1.
        PerfCounters.WatchWpf(Dispatcher);
        // Braço «puro» (sem a animação do ponto «a tocar»): MainWindow.Media.cs, SetUpLiveDot.
    }
}
