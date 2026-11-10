using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Eteris.Browser.Browser;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser;

// O computador novo a entrar na conta (0.2.15, T7): o alerta «conta substituída» (candidata AccountReplaced,
// prioridade 2) por cima da página. Esta parte só diz se quer aparecer e mostra-a ou esconde-a quando o ponto único das
// barras manda (RefreshBars); nunca mexe na visibilidade por conta própria, nunca pede o foco. O estado vem do agente
// (o mesmo em todas as janelas normais). Janelas privadas: nada (WireSignInBar não chega cá).
public partial class MainWindow
{
    private GuestPairingBar? _guestBar;                              // criado no primeiro show(true)
    private GuestPairingView _guestView = GuestPairingView.None;     // o que o último «wants» viu
    private Thickness _guestBarMargins;

    // Chamado dentro de WireSignInBar() (depois de «if (IsPrivate) return;» e antes do RefreshBars final).
    private void WireGuestPairing()
    {
        RegisterBar(Sync.WindowBar.AccountReplaced, ReplacedWants, ShowReplaced);
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
