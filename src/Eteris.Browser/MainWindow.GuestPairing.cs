using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser;

// O computador novo a entrar na conta (0.2.15, T7): o alerta «conta substituída» (candidata AccountReplaced,
// prioridade 2) e o cartão do pedido de aprovação (na vaga 3, partilhada com a barra do início de sessão) por cima da
// página. Os dois usam o mesmo controlo. Esta parte só diz se quer aparecer e mostra-a ou esconde-a quando o ponto único das
// barras manda (RefreshBars); nunca mexe na visibilidade por conta própria, nunca pede o foco. O estado vem do agente
// (o mesmo em todas as janelas normais). Janelas privadas: nada (WireSignInBar não chega cá).
public partial class MainWindow
{
    private GuestPairingBar? _guestBar;                              // criado no primeiro show(true)
    private GuestPairingView _guestView = GuestPairingView.None;     // o que o último «wants» viu
    private DispatcherTimer? _guestSeen;                             // 5 s, só com o cartão em «à espera» à vista
    private Thickness _guestBarMargins;

    // Chamado dentro de WireSignInBar() (depois de «if (IsPrivate) return;» e antes do RefreshBars final). A vaga 3
    // (SignInSlotWants/ShowSignInSlot) é registada lá, no lugar da barra do início de sessão.
    private void WireGuestPairing()
    {
        RegisterBar(Sync.WindowBar.AccountReplaced, ReplacedWants, ShowReplaced);
        Closed += (_, _) => _guestSeen?.Stop();
    }

    // O cartão quer a vaga 3: há um pedido (ou o fim dele, ou acabou de entrar) e não foi recolhido. Só memória.
    private bool GuestCardWants()
    {
        try { _guestView = SyncService.GuestPairingState(); }
        catch (Exception) { _guestView = GuestPairingView.None; }
        return GuestPairingModel.CardWants(_guestView, SyncService.GuestCardOpen);
    }

    // A vaga 3 é uma só candidata: a barra do início de sessão OU o cartão. Os dois são sempre avaliados (cada um
    // guarda a sua vista).
    private bool SignInSlotWants()
    {
        var card = GuestCardWants();
        var bar = SignInBarWants();
        return card || bar;
    }

    private void ShowSignInSlot(bool on)
    {
        // A entrada em curso, ou o resultado dela, tem a vez (com o cartão a tratar do estado, a barra não diz nada).
        var bar = on && _signInView is not null;
        var card = on && !bar;
        if (!card) ShowGuestBar(false);
        ShowSignInBar(bar);
        if (card) ShowGuestBar(true);
    }

    // Barato e sem exceções: só memória (lê o campo do agente; nunca o cria).
    private bool ReplacedWants()
    {
        try { _guestView = SyncService.GuestPairingState(); }
        catch (Exception) { _guestView = GuestPairingView.None; }
        return GuestPairingModel.ReplacedWants(_guestView, SyncService.ReplacedAlertDismissed);
    }

    private void ShowReplaced(bool on) => ShowGuestBar(on);

    // O controlo é partilhado pelo alerta e pelo cartão: com false esconde sem criar nada; com true cria no primeiro uso
    // e desenha a última vista.
    private void ShowGuestBar(bool on)
    {
        if (!on)
        {
            _guestSeen?.Stop();
            if (_guestBar is not null) _guestBar.Visibility = Visibility.Collapsed;
            return;
        }
        if (_guestBar is null)
        {
            var bar = new GuestPairingBar
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(bar, 2);
            Panel.SetZIndex(bar, 50);
            bar.Confirmed += () => GuestAction(() => SyncService.Agent()?.ConfirmPairing());
            // «Não»: cancela o pedido (fica «Ainda à espera?»).
            bar.Denied += () => GuestAction(() => SyncService.Agent()?.CancelPairing());
            bar.AskAgain += () => GuestAction(() => SyncService.AskPairingAgain());
            // «Não tenho o outro computador à mão»: o pedido para e as Definições abrem onde está o campo do kit.
            bar.NoOtherComputer += () => GuestAction(() =>
            {
                SyncService.Agent()?.CancelPairing();
                OpenInternal(SettingsPage.Url + "#sincronizacao");
            });
            // × «Fechar»: recolhe o cartão em todas as janelas (a barra do início de sessão volta a «Ver código»).
            bar.CloseRequested += () => GuestAction(() => SyncService.SetGuestCard(false));
            bar.ReplacedJoin += OnReplacedJoin;
            bar.ReplacedNotMe += () =>
            {
                // «Não fui eu»: nada sai para a rede; a barra do alerta não volta nesta abertura e as Definições abrem
                // onde se retira o acesso do Éteris.
                SyncService.DismissReplacedAlert();
                OpenInternal(SettingsPage.Url + SyncService.RevokeAnchor);
                AfterGuestAction();
            };
            AddToOverlay(bar);
            _guestBar = bar;
        }
        _guestBar.Show(_guestView);
        PlaceGuestBar(_guestBarMargins);
        _guestBar.Visibility = Visibility.Visible;
        // O cartão em «à espera» à vista deixa o pedido renovar-se (só memória, sem rede); nos outros estados, não.
        if (_guestView.Kind == GuestPairingKind.Waiting) StartGuestSeen();
        else _guestSeen?.Stop();
    }

    private void StartGuestSeen()
    {
        if (WindowState != WindowState.Minimized) SyncService.PairingScreenSeen();
        if (_guestSeen is { IsEnabled: true }) return;
        if (_guestSeen is null)
        {
            _guestSeen = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            _guestSeen.Tick += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) SyncService.PairingScreenSeen();
            };
        }
        _guestSeen.Start();
    }

    // Uma ação do cartão; depois, o ponto único decide e o teclado volta à página.
    private void GuestAction(Action action)
    {
        action();
        AfterGuestAction();
    }

    // «Foi eu — juntar este computador»: a confirmação do Windows desta janela (sempre, com segredos ligados aqui).
    private async void OnReplacedJoin()
    {
        _guestBar?.ShowNote(null);
        bool ok;
        try
        {
            ok = await SyncService.JoinAfterReplacedAsync(purpose => ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken()));
        }
        catch (OperationCanceledException) { ok = false; }
        catch (Exception e)
        {
            AfterGuestAction();
            _guestBar?.ShowNote("Não foi possível concluir (" + e.Message + ").");
            return;
        }
        AfterGuestAction();
        if (!ok && SyncService.GuestPairingState().Kind == GuestPairingKind.Replaced)
            _guestBar?.ShowNote(SyncChoices.NotConfirmed("este computador não foi juntado à conta"));
    }

    // Depois de cada ação o «wants» pode ter mudado: o ponto único decide; o teclado volta à página.
    private void AfterGuestAction()
    {
        RefreshBars();
        ReturnFocusToPage();
    }

    // A moldura da página (barra lateral): chamado em MainWindow.Sidebar.cs com as outras barras. Ao centro, no cimo,
    // por baixo do pedido de permissão ou do procurar quando estão à vista (como as outras barras).
    private void PlaceGuestBar(Thickness margins)
    {
        _guestBarMargins = margins;
        if (_guestBar is null) return;
        var top = 10.0;
        foreach (var other in new FrameworkElement[] { PermissionBar, FindBar })
            if (other.IsVisible && other.ActualHeight > 0)
                top = Math.Max(top, other.Margin.Top + other.ActualHeight + 8);
        _guestBar.Margin = new Thickness(margins.Left + 16, top, margins.Right + 16, 0);
    }
}
