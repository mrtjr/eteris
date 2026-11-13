using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Watching;

namespace Eteris.Browser;

// A vigia de páginas (Watching/): um relógio de 1 min na janela dona da sessão visita as vigias com a hora chegada, uma de
// cada vez, no motor escondido (WatchRunner), e larga o motor no fim de cada lote. A 1.ª ronda é 60 s depois de a janela
// abrir (as vigias em atraso correm depois de o arranque assentar). Nada corre com o Éteris fechado (decisão 2), nunca na
// janela privada, e o motor vai por omissão num contexto só em memória, sem os cookies do utilizador (decisão 1).
public partial class MainWindow
{
    private DispatcherTimer? _watchTimer;
    private WatchRunner? _watchRunner;
    private CancellationTokenSource? _watchCancel;
    private bool _watchBusy;

    // A vigia das provas ao vivo (ETERIS_TEST_WATCH) cria-se uma vez por processo, e só se ainda não existir.
    private static bool s_testWatchSeeded;

    // Só a janela dona da sessão vigia, como as abas adiadas (senão duas janelas visitavam a mesma página).
    private void WatchPages()
    {
        if (!_ownsSession || IsPrivate || _watchTimer is not null) return;
        SeedTestWatch();
        var cancel = _watchCancel = new CancellationTokenSource();
        var period = TestSwitches.WatchSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Min(seconds, 60)) : TimeSpan.FromMinutes(1);
        _watchTimer = new DispatcherTimer(period, DispatcherPriority.Background, (_, _) => _ = CheckDueWatchesAsync(cancel.Token), Dispatcher);
        _watchTimer.Start();
        Closed += (_, _) =>
        {
            _watchTimer.Stop();
            cancel.Cancel();
            _watchRunner?.Dispose();
            _watchRunner = null;
        };
    }

    private void SeedTestWatch()
    {
        if (s_testWatchSeeded || TestSwitches.WatchSeed is not { } seed) return;
        s_testWatchSeeded = true;
        if (AppData.WatchesFor(seed.Url).Count > 0) return;
        AppData.AddWatch(new NewWatch(seed.Url, seed.Url, WatchKind.Price, seed.Selector, UseSession: false,
            WatchSchedule.MinIntervalMinutes, TargetPrice: null), IsPrivate);
    }

    // Um lote: as vigias com a hora chegada, uma de cada vez. Um tique a meio de um lote não começa outro.
    private async Task CheckDueWatchesAsync(CancellationToken ct)
    {
        if (_watchBusy || ct.IsCancellationRequested) return;
        var due = AppData.DueWatches(DateTime.UtcNow);
        if (due.Count == 0) return;
        _watchBusy = true;
        try
        {
            _watchRunner ??= new WatchRunner(WatchContext, () => new PrivacyRequestHandler(() => ShieldEngine.Current, ShieldOn,
                onBlocked: _ => { }, isPrivate: false, localNetworkAllowed: IsLocalNetworkAllowed, chooseCertificate: RefuseClientCertificate));
            foreach (var watch in due)
            {
                if (ct.IsCancellationRequested) break;
                await CheckWatchAsync(_watchRunner, watch, ct);
            }
        }
        finally
        {
            _watchRunner?.Release();
            _watchBusy = false;
        }
    }

    // Sem «com a minha sessão», um contexto só em memória da janela (o motor larga-o no fim do lote); com ela, o do perfil.
    private IRequestContext WatchContext(bool useSession)
        => useSession ? Cef.GetGlobalRequestContext() : CreatePrivateContext(onBlocked: null);

    // Uma verificação: ler, passar a verificação, gravar com a próxima hora e, se a vigia ainda existe, avisar.
    private async Task CheckWatchAsync(WatchRunner runner, PageWatch before, CancellationToken ct)
    {
        var (reading, block) = await runner.ReadAsync(before.Url, before.Selector, before.Kind, before.UseSession, ct);
        // A janela fechou a meio: a leitura interrompida não conta como falha.
        if (ct.IsCancellationRequested) return;
        var now = DateTime.UtcNow;
        var check = WatchRules.ToCheck(before, reading, block, now);
        // A agenda conta as falhas como o WatchStore as vai gravar.
        var failures = WatchRules.IsFailure(check) ? before.Failures + 1 : 0;
        var next = TestSwitches.WatchSeconds is { } seconds && check.Status is not (WatchStatus.Blocked or WatchStatus.Missing)
            ? now.AddSeconds(seconds)
            : WatchSchedule.Next(now, before.IntervalMinutes, failures, check.Status, Random.Shared);
        // A vigia foi apagada a meio da visita: nada se grava e nada se avisa.
        if (!AppData.RecordWatch(before.Id, check, next)) return;
        NotifyWatch(before, WatchRules.Outcome(before, check), check);
    }

    // Os avisos (o aviso interno e o piscar na barra de tarefas) chegam com a Task 7 do plano da etapa A.
    private void NotifyWatch(PageWatch before, WatchOutcome outcome, WatchCheck check)
    {
    }
}
