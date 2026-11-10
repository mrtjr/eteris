using System.Windows;
using System.Windows.Threading;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// E13 do plano de desempenho da 0.2.14 (ajuste e13, ligado por omissão pela decisão D-6): de 15 em 15 s, com pouca
// memória (Tabs/MemoryPressure.cs: o aviso do Windows, o commit livre, ou o sinal simulado ETERIS_TEST_PRESSAO das
// medições), adormece UMA aba entre todas as janelas: a acordada usada há mais tempo que não está isenta. Um só
// temporizador para o processo (as janelas partilham o fio da interface), para N janelas não adormecerem N abas.
public partial class MainWindow
{
    private static DispatcherTimer? _pressureTimer;

    private sealed record PressureTarget(MainWindow Window, BrowserTab Tab);

    private void WatchMemoryPressure()
    {
        if (_pressureTimer is not null || !PerfTuning.IsOn(PerfTuning.SleepUnderPressure)) return;
        _pressureTimer = new DispatcherTimer(MemoryPressure.Interval, DispatcherPriority.Background, (_, _) => SleepOneUnderPressure(), Dispatcher);
        _pressureTimer.Start();
    }

    private static void SleepOneUnderPressure()
    {
        if (!MemoryPressure.Applies(Data.Settings.Current) || !MemoryPressure.IsLowNow()) return;
        var candidates = Application.Current.Windows.OfType<MainWindow>().SelectMany(w => w.PressureCandidates()).ToList();
        if (MemoryPressure.Pick(candidates) is not { } target) return;
        PerfCounters.Add("sono.pressao");
        target.Window.PutToSleep(target.Tab);
    }

    // As abas desta janela com motor (na faixa e nas reservas dos outros Espaços), com o que as isenta.
    private IEnumerable<MemoryPressure.Candidate<PressureTarget>> PressureCandidates()
        => Tabs.Tabs.Concat(Tabs.AwakeOutOfView())
            .Where(tab => !tab.IsSleeping && HasEngine(tab) && !_fallingAsleep.Contains(tab))
            .Select(tab => new MemoryPressure.Candidate<PressureTarget>(new PressureTarget(this, tab),
                OnScreen: IsTabOnScreen(tab), Loading: tab.IsLoading, Audible: tab.IsAudible, Locked: tab.IsLocked,
                Pinned: tab.IsPinned, Capturing: _browsers.TryGetValue(tab, out var browser) && IsCapturing(browser),
                Playing: tab.Media == MediaState.Playing, LastActiveUtc: tab.LastActiveUtc));
}
