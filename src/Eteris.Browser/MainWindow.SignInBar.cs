using System.Windows;
using System.Windows.Controls;
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

    // ÚNICO sítio que mostra ou esconde barras da janela. Só na thread da IU.
    private void RefreshBars() => _bars.Refresh();

    // Chamado no ContentRendered (MainWindow.xaml.cs), depois de a camada existir.
    private void WireSignInBar()
    {
        if (IsPrivate) return;
        RegisterBar(WindowBar.SignIn, SignInBarWants, ShowSignInBar);
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
        // O pedido de permissão de um site e o procurar na página nunca ficam tapados: a barra desce para baixo deles.
        PermissionBar.SizeChanged += (_, _) => PlaceSignInBar(_signInBarMargins);
        FindBar.SizeChanged += (_, _) => PlaceSignInBar(_signInBarMargins);
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
        if (progress.Stage == SignInStage.Done)
        {
            try
            {
                sync = SyncService.Agent()?.Status;
                // O nome; o e-mail só quando não há nome.
                var account = GoogleSync.Account;
                name = string.IsNullOrWhiteSpace(account?.Name) ? account?.Email : account.Name;
            }
            catch (Exception)
            {
                // Sem estado: a barra fica em «A sincronizar…», que desaparece quando a ronda acabar ou falhar.
            }
        }
        _signInView = SignInBarModel.For(new SignInBarInput(progress, sync, name, IsPrivate));
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
            case SignInBarAction.ShowCode:
                // «Ver código»: provisório até ao cartão do convidado (T7); o código já aparece nas Definições.
                OpenInternal(SettingsPage.Url + "#sincronizacao");
                GoogleSync.ClearSignIn(_signInAtMs);
                break;
            case SignInBarAction.Close:
                GoogleSync.ClearSignIn(_signInAtMs);
                break;
        }
        RefreshBars();
        ReturnFocusToPage();
    }
}
