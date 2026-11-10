using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Eteris.Browser.Sync;

// O cartão do computador novo e o alerta «conta substituída» (0.2.15, T7): desenha uma GuestPairingView e levanta
// eventos. Não conhece a sincronização, não se mostra nem se esconde a si própria (isso é do ponto único da janela,
// RefreshBars) e nunca pede o foco do teclado: não há botão por omissão e nenhuma tecla confirma nada. «Sim» só conta
// 2 s depois de o código aparecer (GuestPairingModel.ConfirmDelayMs).
internal sealed partial class GuestPairingBar : UserControl
{
    private readonly Func<long> _now;
    private GuestPairingView? _view;
    private string? _shownCode;     // o código à vista (a espera de «Sim» recomeça quando muda)
    private long _codeShownMs;
    private DispatcherTimer? _yesTimer;

    public GuestPairingBar(Func<long>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        InitializeComponent();
        YesButton.Content = GuestPairingModel.Yes;
        NoButton.Content = GuestPairingModel.No;
        AskAgainButton.Content = GuestPairingModel.AskAgain;
        ReplacedJoinButton.Content = GuestPairingModel.ReplacedJoin;
        ReplacedNotMeButton.Content = GuestPairingModel.ReplacedNotMe;
        NoOtherLink.Content = GuestPairingModel.NoOther;
        CloseButton.ToolTip = GuestPairingModel.Close;
        AutomationProperties.SetName(CloseButton, GuestPairingModel.Close);
        // Os leitores de ecrã anunciam o que muda (o cartão aparece sem levar o foco).
        AutomationProperties.SetLiveSetting(TitleText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(BodyText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(NoteText, AutomationLiveSetting.Polite);
        Wire(YesButton, () =>
        {
            // Um toque nos primeiros 2 s depois de o código aparecer não confirma (nem fica guardado para depois).
            if (_view?.Kind == GuestPairingKind.Code && GuestPairingModel.MayConfirm(_codeShownMs, _now())) Confirmed?.Invoke();
        });
        Wire(NoButton, () => Denied?.Invoke());
        Wire(AskAgainButton, () => AskAgain?.Invoke());
        Wire(NoOtherLink, () => NoOtherComputer?.Invoke());
        Wire(ReplacedJoinButton, () => ReplacedJoin?.Invoke());
        Wire(ReplacedNotMeButton, () => ReplacedNotMe?.Invoke());
        Wire(CloseButton, () => CloseRequested?.Invoke());
    }

    public event Action? Confirmed;         // «Sim»
    public event Action? Denied;            // «Não»
    public event Action? AskAgain;          // «Pedir outra vez»
    public event Action? NoOtherComputer;   // «Não tenho o outro computador à mão»
    public event Action? CloseRequested;    // × «Fechar» (recolhe o cartão; só nos estados do cartão)
    public event Action? ReplacedJoin;      // «Fui eu — juntar este computador»
    public event Action? ReplacedNotMe;     // «Não fui eu»

    // Desenha a vista. Nunca chama Focus(). Uma nota (P4, erro de «Fui eu») fica enquanto o estado for o mesmo.
    public void Show(GuestPairingView view)
    {
        if (_view?.Kind != view.Kind) ShowNote(null);
        _view = view;
        string? title = null, code = null, body = null, line = null, consequence = null;
        bool yes = false, no = false, again = false, join = false, notMe = false, link = true, close = true;
        switch (view.Kind)
        {
            case GuestPairingKind.Waiting:
                (title, body, line) = (GuestPairingModel.WaitTitle, GuestPairingModel.WaitText, GuestPairingModel.MustBeOpen);
                break;
            case GuestPairingKind.Code when !view.Confirmed:
                (title, code, body) = (GuestPairingModel.TypeThisCode, view.Code, GuestPairingModel.Ask(view.Code ?? string.Empty));
                yes = no = true;
                break;
            case GuestPairingKind.Code:
                (title, code, body) = (GuestPairingModel.TypeThisCode, view.Code, GuestPairingModel.WaitingApproval);
                link = false;
                break;
            case GuestPairingKind.Joined:
                body = GuestPairingModel.Joined;
                link = false;
                break;
            case GuestPairingKind.TimeUp:
                title = GuestPairingModel.TimeUp;
                again = true;
                break;
            case GuestPairingKind.StillWaiting:
                (title, line) = (GuestPairingModel.StillWaiting, GuestPairingModel.MustBeOpen);
                again = true;
                break;
            case GuestPairingKind.Refused:
            case GuestPairingKind.Failed:
                body = view.Error;
                again = true;
                break;
            case GuestPairingKind.Replaced:
                (title, body, consequence) = (GuestPairingModel.ReplacedTitle, GuestPairingModel.ReplacedText, GuestPairingModel.ReplacedConsequence);
                join = notMe = true;
                // O alerta não se recolhe: só «Não fui eu» o esconde.
                link = close = false;
                break;
            default:
                link = close = false;
                break;
        }
        ArmYes(view.Kind == GuestPairingKind.Code && !view.Confirmed ? view.Code : null);
        Text(TitleText, title);
        Text(CodeText, code);
        Text(BodyText, body);
        Text(LineText, line);
        Text(ConsequenceText, consequence);
        Visible(YesButton, yes);
        Visible(NoButton, no);
        Visible(AskAgainButton, again);
        Visible(ReplacedJoinButton, join);
        Visible(ReplacedNotMeButton, notMe);
        Actions.Visibility = Of(yes || no || again || join || notMe);
        Visible(NoOtherLink, link);
        Visible(CloseButton, close);
        AutomationProperties.SetName(this, title ?? body ?? string.Empty);
    }

    // P4 e os erros de «Fui eu»; null limpa.
    public void ShowNote(string? text) => Text(NoteText, text);

    // Um código novo à vista: a espera de «Sim» recomeça e o botão desenha-se desativado nesses 2 s (um temporizador de
    // um só disparo volta a ativá-lo; a decisão é sempre a do relógio, em MayConfirm). O mesmo código não recomeça.
    private void ArmYes(string? code)
    {
        if (code == _shownCode) return;
        _shownCode = code;
        _yesTimer?.Stop();
        if (code is null)
        {
            YesButton.IsEnabled = true;
            return;
        }
        _codeShownMs = _now();
        YesButton.IsEnabled = false;
        _yesTimer ??= NewYesTimer();
        _yesTimer.Interval = TimeSpan.FromMilliseconds(GuestPairingModel.ConfirmDelayMs);
        _yesTimer.Start();
    }

    private DispatcherTimer NewYesTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            YesButton.IsEnabled = true;
        };
        return timer;
    }

    private static void Text(TextBlock block, string? text)
    {
        if (block.Text != (text ?? string.Empty)) block.Text = text ?? string.Empty;
        block.Visibility = Of(text is not null);
    }

    private static void Visible(UIElement element, bool on) => element.Visibility = Of(on);

    private static Visibility Of(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // Ao premir (não ao largar), como nas outras barras: a camada não está ativa e o primeiro clique numa janela
    // inativa pode perder o «largar». Pela automação (leitor de ecrã), o Click normal.
    private static void Wire(Button button, Action action)
    {
        if (AutomationProperties.GetName(button) is not { Length: > 0 }) AutomationProperties.SetName(button, button.Content as string ?? string.Empty);
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        button.Click += (_, _) => action();
    }
}
