using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Engine;

namespace Eteris.Browser;

// As barras da janela por cima da página (0.2.15): o ponto único que as mostra ou esconde, uma de cada vez
// (WindowBarSet, por prioridade), e a primeira candidata, a barra do início de sessão na Google. O estado é estático
// e partilhado (GoogleSync.Progress): todas as janelas normais mostram a mesma barra; cada janela tem o seu conjunto
// de barras e o seu controlo. Janelas privadas: nada. Sem início de sessão em curso não se cria nada (regra R-2).
public partial class MainWindow
{
    private readonly WindowBarSet _bars = new();
    private SignInBar? _signInBar;
    private DispatcherTimer? _signInBarHide;
    private SignInBarView? _signInView;      // a última vista calculada para a candidata SignIn
    private long _signInAtMs;                // o AtMs do estado a que essa vista pertence
    private (SignInBarKind Kind, long AtMs)? _signInTimed; // o estado para que o «some sozinha» já está armado
    private Thickness _signInBarMargins;

    // As outras barras (sessão expirada, pedido de entrada, conta substituída, kit, oferta da T10) registam aqui a sua
    // candidata, uma vez por janela, a partir dos seus parciais.
    private void RegisterBar(WindowBar kind, Func<bool> wants, Action<bool> show) => _bars.Register(kind, wants, show);

    // Posto no Closed (MainWindow.PairingBar.cs, InitPairingBar): um RefreshBars já na fila, ou o fim de uma espera
    // assíncrona de uma barra, não volta a mostrar nada numa janela fechada (a camada recusava o Show e o Éteris caía).
    private bool _barsClosed;

    // ÚNICO sítio que mostra ou esconde barras da janela. Só na thread da IU.
    private void RefreshBars()
    {
        if (_barsClosed) return;
        _bars.Refresh();
    }

    // Chamado no ContentRendered (MainWindow.xaml.cs), depois de a camada existir.
    private void WireSignInBar()
    {
        if (IsPrivate) return;
        // A vaga 3 é partilhada com o cartão do computador novo (T7, MainWindow.GuestPairing.cs): uma só candidata.
        RegisterBar(Sync.WindowBar.SignIn, SignInSlotWants, ShowSignInSlot);
        // A sessão expirada (T11): o aviso é partilhado por todas as janelas normais (SyncService), chega por
        // SyncService.Changed e responder numa janela fecha-o em todas.
        RegisterBar(WindowBar.SessionExpired, () => SyncService.SignInPrompt,
            on => ShowInSignInBar(on ? SignInBarModel.Expired(true, IsPrivate) : null));
        // O lembrete do kit de recuperação (T5): a prioridade mais baixa; partilhado por todas as janelas normais
        // (SyncService), responder numa fecha-o em todas. As rondas levantam SyncService.Changed: sem temporizador.
        RegisterBar(Sync.WindowBar.KitReminder,
            () => SyncService.KitBar(IsPrivate) is not null,
            on => ShowInSignInBar(on ? Sync.SignInBarModel.KitReminder(SyncService.KitBar(IsPrivate)) : null));
        // A oferta única «receber também neste?» (T10): a prioridade mais baixa; lê o agente que já existe (nunca o
        // cria). As rondas levantam SyncService.Changed: sem subscrição própria nem temporizador.
        RegisterBar(Sync.WindowBar.SecretsOffer,
            () => SyncService.SecretsOfferPrompt,
            on => ShowInSignInBar(on ? Sync.SignInBarModel.SecretsOffer(true, IsPrivate) : null));
        // Os eventos chegam de qualquer thread; um só manipulador para os três.
        void changed() => Dispatcher.BeginInvoke(RefreshBars);
        GoogleSync.SignInChanged += changed;
        SyncService.Changed += changed;
        GoogleSync.AccountChanged += changed;
        Closed += (_, _) =>
        {
            GoogleSync.SignInChanged -= changed;
            SyncService.Changed -= changed;
            GoogleSync.AccountChanged -= changed;
            _signInBarHide?.Stop();
        };
        // O pedido de permissão de um site e o procurar na página nunca ficam tapados: as barras descem para baixo deles
        // quando aparecem ou mudam de tamanho, e sobem quando saem.
        foreach (var other in new FrameworkElement[] { PermissionBar, FindBar })
        {
            other.SizeChanged += (_, _) => PlaceBarsBelowPageBars();
            other.IsVisibleChanged += (_, _) => PlaceBarsBelowPageBars();
        }
        // O computador novo (T7): o alerta «conta substituída» e o cartão do pedido de aprovação.
        WireGuestPairing();
        // Uma janela aberta a meio de um início de sessão mostra logo a barra; com tudo parado, nada se cria nem se lê.
        RefreshBars();
    }

    private bool SignInBarWants()
    {
        var progress = GoogleSync.Progress;
        if (progress.Stage == SignInStage.Idle)
        {
            _signInView = null;
            return false;
        }
        SyncStatus? sync = null;
        string? name = null;
        var guest = false;
        (string? Text, SignInBarButton? Link) hooks = (null, null);
        if (progress.Stage == SignInStage.Done)
        {
            try
            {
                sync = SyncService.Agent()?.Status;
                // «Sincronizado.»: o aviso «Recebeu…» do computador que herdou pagamentos e documentos, ou a ligação
                // «Também pagamentos e documentos…» no que criou a conta (T10).
                hooks = Sync.SyncChoices.SyncedHooks(sync);
                // O nome; o e-mail só quando não há nome.
                var account = GoogleSync.Account;
                name = string.IsNullOrWhiteSpace(account?.Name) ? account?.Email : account.Name;
                // O cartão do computador novo (ou o alerta) trata deste estado: a barra não diz «Falta confirmar…».
                guest = GuestPairingModel.Handles(SyncService.GuestPairingState(), SyncService.GuestCardOpen);
            }
            catch (Exception)
            {
                // Sem estado: a barra fica em «A sincronizar…», que desaparece quando a ronda acabar ou falhar.
            }
        }
        _signInView = SignInBarModel.For(new SignInBarInput(progress, sync, name, IsPrivate)
        {
            GuestPairing = guest,
            SyncedText = hooks.Text,
            SyncedLink = hooks.Link,
        });
        _signInAtMs = progress.AtMs;
        return _signInView is not null;
    }

    private void ShowSignInBar(bool on)
    {
        if (!on || _signInView is not { } view)
        {
            ShowInSignInBar(null);
            return;
        }
        ShowInSignInBar(view);
        if (view.HideAfter is not { } hold)
        {
            // A vista seguinte não some sozinha: o temporizador da anterior não fica armado.
            _signInBarHide?.Stop();
            _signInTimed = null;
            return;
        }
        // Arma-se uma vez por estado (não a cada RefreshBars). Ao disparar, limpa só o estado a que pertence.
        if (_signInTimed == (view.Kind, _signInAtMs)) return;
        _signInTimed = (view.Kind, _signInAtMs);
        var atMs = _signInAtMs;
        _signInBarHide?.Stop();
        _signInBarHide = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = hold };
        _signInBarHide.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            GoogleSync.ClearSignIn(atMs);
        };
        _signInBarHide.Start();
    }

    // O controlo é partilhado pelas candidatas que mostram uma SignInBarView (início de sessão, sessão expirada, kit):
    // com uma vista, mostra-a (criando o controlo no primeiro uso); com null, esconde-o sem criar nada.
    private void ShowInSignInBar(SignInBarView? view)
    {
        if (view is null)
        {
            if (_signInBar is not null) _signInBar.Visibility = Visibility.Collapsed;
            return;
        }
        if (_signInBar is null)
        {
            _signInBar = new SignInBar
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(_signInBar, 2);
            Panel.SetZIndex(_signInBar, 50);
            _signInBar.Acted += OnSignInBarAction;
            AddToOverlay(_signInBar);
            PlaceSignInBar(_signInBarMargins);
        }
        _signInBar.Show(view);
        _signInBar.Visibility = Visibility.Visible;
    }

    // As três barras por cima da página (início de sessão, aprovação, computador novo), com as margens guardadas.
    private void PlaceBarsBelowPageBars()
    {
        PlaceSignInBar(_signInBarMargins);
        PlacePairingBar(_pairingBarMargins);
        PlaceGuestBar(_guestBarMargins);
    }

    // A moldura da página (barra lateral): chamado em MainWindow.Sidebar.cs com as outras barras.
    private void PlaceSignInBar(Thickness margins)
    {
        _signInBarMargins = margins;
        if (_signInBar is null) return;
        var top = 10.0;
        foreach (var other in new FrameworkElement[] { PermissionBar, FindBar })
            if (other.IsVisible && other.ActualHeight > 0)
                top = Math.Max(top, other.Margin.Top + other.ActualHeight + 8);
        _signInBar.Margin = new Thickness(margins.Left + 16, top, margins.Right + 16, 0);
    }

    private void OnSignInBarAction(SignInBarAction action)
    {
        switch (action)
        {
            case SignInBarAction.Cancel:
                GoogleSync.CancelSignIn();
                break;
            case SignInBarAction.CopyAddress:
                // Pela área de transferência segura (fora do histórico e da nuvem do Windows, limpa ao fim de 60 s): o
                // endereço leva o «state», o desafio PKCE e, na reentrada, o e-mail.
                if (GoogleSync.Progress.ManualUrl is { } url && SecureClipboard.Shared.Copy(url)) ShowToast("Endereço copiado");
                break;
            case SignInBarAction.Retry:
                GoogleSync.RetrySignIn();
                break;
            case SignInBarAction.OpenSettings:
                OpenInternal(SettingsPage.Url + "#sincronizacao");
                GoogleSync.ClearSignIn(_signInAtMs);
                break;
            case SignInBarAction.ShowCode:
                // «Ver código» abre o cartão do computador novo (T7), sem limpar a entrada: recolhido outra vez, volta
                // esta forma. Sem nada no cartão, as Definições, como antes.
                if (SyncService.GuestPairingState().Kind is GuestPairingKind.None or GuestPairingKind.Replaced)
                {
                    OpenInternal(SettingsPage.Url + "#sincronizacao");
                    GoogleSync.ClearSignIn(_signInAtMs);
                }
                else SyncService.SetGuestCard(true);
                break;
            case SignInBarAction.Close:
                GoogleSync.ClearSignIn(_signInAtMs);
                break;
            case SignInBarAction.SignInAgain:
                // A conta guardada vai como login_hint (só no endereço de autorização da Google). A barra do início de
                // sessão toma o lugar desta («À espera…»); se já houver uma entrada em curso, o BeginSignIn não faz nada.
                SyncService.DismissSignInPrompt();
                GoogleSync.BeginSignIn(GoogleSync.ReentryHint());
                break;
            case SignInBarAction.NotNow:
                SyncService.DismissSignInPrompt();
                break;
            case SignInBarAction.SaveKit:
                // Não mostra o kit: quem o mostra é a secção das Definições, depois da confirmação do Windows.
                SyncService.KitBarAnswered();
                OpenInternal(SettingsPage.Url + "#syncKitItem");
                break;
            case SignInBarAction.KitLater:
                SyncService.SnoozeKit();
                break;
            case SignInBarAction.KitDecline:
                SyncService.DeclineKit();
                break;
            case SignInBarAction.AccountSecrets:
                // A linha da escolha da conta nas Definições: a pessoa lê a descrição antes de o Windows aparecer.
                OpenInternal(SettingsPage.Url + "#syncTopicItem");
                GoogleSync.ClearSignIn(_signInAtMs);
                break;
            case SignInBarAction.OfferYes:
                // Não bloqueia: o Windows Hello responde depois; as barras refrescam-se sozinhas (SyncService.Changed).
                AnswerSecretsOfferFromBar(true);
                break;
            case SignInBarAction.OfferNo:
                AnswerSecretsOfferFromBar(false);
                break;
            case SignInBarAction.OfferLater:
                SyncService.CloseSecretsOffer();
                break;
        }
        RefreshBars();
        ReturnFocusToPage();
    }

    // A oferta única. «Sim»: o Windows Hello desta janela; recusado ou falhado, a oferta fica e o aviso diz porquê.
    // «Não»: sem Windows. Uma falha mostra um texto fixo (a mensagem da exceção pode trazer caminhos locais).
    private async void AnswerSecretsOfferFromBar(bool accept)
    {
        string? error;
        try
        {
            error = accept
                ? await SyncService.AnswerSecretsOfferAsync(true,
                    purpose => ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken()))
                : await SyncService.AnswerSecretsOfferAsync(false, _ => Task.FromResult(false));
        }
        catch (OperationCanceledException) { error = SyncChoices.NotConfirmed($"«{SyncChoices.AccountLine}» continua desligado"); }
        catch (Exception) { error = SyncChoices.CouldNotFinish; }
        // A janela pode ter fechado durante o Windows Hello: o aviso não vai para uma janela fechada.
        if (_barsClosed) return;
        if (error is not null) ShowToast(error);
    }
}
