using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Pairing;

namespace Eteris.Browser;

// A barra de aprovação (0.2.15): um computador novo pede para entrar na conta e a pessoa escreve aqui o código que ele
// mostra. É uma candidata do ponto único das barras (prioridade 1): esta parte só diz se quer aparecer e mostra-a ou
// esconde-a quando o ponto único manda. O estado vem do agente da sincronização (o mesmo em todas as janelas normais);
// o conselho depois de uma recusa é só desta janela. Janelas privadas: nada.
public partial class MainWindow
{
    private PairingBar? _pairingBar;        // criado no primeiro show(true); nunca no arranque
    private PairingBarView? _pairingView;   // o que o último «wants» viu
    private string? _pairingAdvice;         // conselho (recusa) por fechar NESTA janela
    private DispatcherTimer? _pairingTick;  // só enquanto «A ligar…» estiver à vista: para sair aos 60 s
    private Thickness _pairingBarMargins;

    // Chamado no construtor (MainWindow.xaml.cs). Só regista: nada se cria nem se lê aqui.
    private void InitPairingBar()
    {
        if (IsPrivate) return;
        RegisterBar(Sync.WindowBar.PairingRequest, PairingBarWants, ShowPairingBar);
    }

    // Barato e sem exceções: só memória (o estado do agente que já existe; nunca o cria).
    private bool PairingBarWants()
    {
        try { _pairingView = Sync.SyncService.PairingBarState(); }
        catch (Exception) { _pairingView = null; }
        return _pairingAdvice is not null || _pairingView is { Kind: not PairingBarKind.None };
    }

    // O «show» da candidata: com false esconde sem criar nada; com true cria no primeiro uso e desenha.
    private void ShowPairingBar(bool on)
    {
        if (!on)
        {
            StopPairingTick();
            if (_pairingBar is null) return;
            var hadFocus = _pairingBar.BoxHasFocus;
            _pairingBar.Visibility = Visibility.Collapsed;
            // O teclado não fica numa barra escondida.
            if (hadFocus) ReturnFocusToPage();
            return;
        }
        var bar = PairingBarControl();
        var before = bar.ShownSession;
        var boxHadFocus = bar.BoxHasFocus;
        if (_pairingAdvice is { } advice) bar.ShowAdvice(advice);
        else if (_pairingView is { } view) bar.Show(view);
        if (_pairingAdvice is null && _pairingView is { Kind: PairingBarKind.Connecting }) StartPairingTick();
        else StopPairingTick();
        PlacePairingBar(_pairingBarMargins);
        bar.Visibility = Visibility.Visible;
        // O pedido em que se estava a escrever saiu (aprovado, recusado, expirou, outro tomou o lugar): o teclado volta.
        if (boxHadFocus && bar.ShownSession != before) ReturnFocusToPage();
    }

    private PairingBar PairingBarControl()
    {
        if (_pairingBar is { } existing) return existing;
        var bar = new PairingBar
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(bar, 2);
        Panel.SetZIndex(bar, 50);
        bar.CodeEntered += OnPairingCode;
        bar.NotMe += session =>
        {
            // «Não fui eu»: recusa o pedido e fica o conselho (só nesta janela) até «Fechar» ou «Retirar o acesso».
            Sync.SyncService.RefusePairing(session);
            _pairingAdvice = PairingBarModel.RefusedAdvice;
            AfterPairingAction(returnFocus: true);
        };
        bar.LookRequested += () =>
        {
            Sync.SyncService.Look();
            AfterPairingAction(returnFocus: true);
        };
        bar.RevokeRequested += () =>
        {
            _pairingAdvice = null;
            OpenInternal(SettingsPage.Url + Sync.SyncService.RevokeAnchor);
            AfterPairingAction(returnFocus: true);
        };
        bar.AdviceClosed += () =>
        {
            _pairingAdvice = null;
            AfterPairingAction(returnFocus: true);
        };
        // A única chamada de foco desta barra, e só em resposta a um clique da pessoa na caixa: a camada fica ativa
        // para a caixa receber as teclas.
        bar.BoxPressed += () => FocusInOverlay(bar.CodeBox);
        AddToOverlay(bar);
        _pairingBar = bar;
        return bar;
    }

    // A pessoa escreveu o código e carregou em Enter na caixa.
    private async void OnPairingCode(string session, string typed)
    {
        PairingAnswer answer;
        try
        {
            answer = await Sync.SyncService.ApprovePairingAsync(session, typed,
                purpose => ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken()));
        }
        catch (SyncUserException e)
        {
            // Não se entregou (sem rede, o pedido expirou entretanto…): o pedido continua e diz-se porquê.
            AfterPairingAction(returnFocus: false);
            _pairingBar?.ShowNote(e.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            answer = PairingAnswer.NotConfirmed;
        }
        catch (Exception e)
        {
            AfterPairingAction(returnFocus: false);
            _pairingBar?.ShowNote("Não foi possível concluir (" + e.Message + ").");
            return;
        }
        if (answer == PairingAnswer.Refused) _pairingAdvice = PairingBarModel.ThreeWrong;
        // Depois de um código errado, de a porta do Windows ser recusada, de um texto incompleto ou de um 2.º Enter, o
        // teclado fica na caixa (para tentar outra vez); nos outros casos volta à página.
        var stays = answer is PairingAnswer.WrongCode or PairingAnswer.NotConfirmed or PairingAnswer.Incomplete or PairingAnswer.Busy;
        AfterPairingAction(returnFocus: !stays);
        if (answer == PairingAnswer.WrongCode && _pairingView is { Kind: PairingBarKind.Code } view && view.Session == session)
            _pairingBar?.ShowNote(PairingBarModel.WrongCode(view.AttemptsLeft));
        else if (answer == PairingAnswer.NotConfirmed)
            _pairingBar?.ShowNote(SyncChoices.NotConfirmed("o pedido continua à espera"));
    }

    // Depois de cada ação o «wants» pode ter mudado: o ponto único decide (nunca a visibilidade à mão).
    private void AfterPairingAction(bool returnFocus)
    {
        RefreshBars();
        if (returnFocus) ReturnFocusToPage();
    }

    // «A ligar ao computador novo…» sai sozinho aos 60 s: o ponto único volta a perguntar de segundo a segundo (sem
    // rede; assíncrono em relação ao «show»).
    private void StartPairingTick()
    {
        if (_pairingTick is { IsEnabled: true }) return;
        if (_pairingTick is null)
        {
            _pairingTick = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _pairingTick.Tick += (_, _) => RefreshBars();
            Closed += (_, _) => _pairingTick?.Stop();
        }
        _pairingTick.Start();
    }

    private void StopPairingTick() => _pairingTick?.Stop();

    // A moldura da página (barra lateral): chamado em MainWindow.Sidebar.cs com as outras barras. Ao centro, no cimo,
    // por baixo do pedido de permissão ou do procurar quando estão à vista (como a barra do início de sessão).
    private void PlacePairingBar(Thickness margins)
    {
        _pairingBarMargins = margins;
        if (_pairingBar is null) return;
        var top = 10.0;
        foreach (var other in new FrameworkElement[] { PermissionBar, FindBar })
            if (other.IsVisible && other.ActualHeight > 0)
                top = Math.Max(top, other.Margin.Top + other.ActualHeight + 8);
        _pairingBar.Margin = new Thickness(margins.Left + 16, top, margins.Right + 16, 0);
    }
}
