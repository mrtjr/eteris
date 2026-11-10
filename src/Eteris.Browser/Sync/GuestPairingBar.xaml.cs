using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Eteris.Browser.Sync;

// O cartão do computador novo e o alerta «conta substituída» (0.2.15, T7): desenha uma GuestPairingView e levanta
// eventos. Não conhece a sincronização, não se mostra nem se esconde a si própria (isso é do ponto único da janela,
// RefreshBars) e nunca pede o foco do teclado: não há botão por omissão e nenhuma tecla confirma nada.
internal sealed partial class GuestPairingBar : UserControl
{
    private GuestPairingView? _view;

    public GuestPairingBar()
    {
        InitializeComponent();
        ReplacedJoinButton.Content = GuestPairingModel.ReplacedJoin;
        ReplacedNotMeButton.Content = GuestPairingModel.ReplacedNotMe;
        CloseButton.ToolTip = GuestPairingModel.Close;
        AutomationProperties.SetName(CloseButton, GuestPairingModel.Close);
        // Os leitores de ecrã anunciam o que muda (o cartão aparece sem levar o foco).
        AutomationProperties.SetLiveSetting(TitleText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(BodyText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(NoteText, AutomationLiveSetting.Polite);
        Wire(ReplacedJoinButton, () => ReplacedJoin?.Invoke());
        Wire(ReplacedNotMeButton, () => ReplacedNotMe?.Invoke());
        Wire(CloseButton, () => CloseRequested?.Invoke());
    }

    public event Action? CloseRequested;    // × «Fechar» (recolhe o cartão; só nos estados do cartão)
    public event Action? ReplacedJoin;      // «Foi eu — juntar este computador»
    public event Action? ReplacedNotMe;     // «Não fui eu»

    // Desenha a vista. Nunca chama Focus(). Uma nota (P4, erro de «Foi eu») fica enquanto o estado for o mesmo.
    public void Show(GuestPairingView view)
    {
        if (_view?.Kind != view.Kind) ShowNote(null);
        _view = view;
        string? title = null, body = null, consequence = null;
        bool join = false, notMe = false;
        switch (view.Kind)
        {
            case GuestPairingKind.Replaced:
                title = GuestPairingModel.ReplacedTitle;
                body = GuestPairingModel.ReplacedText;
                consequence = GuestPairingModel.ReplacedConsequence;
                join = notMe = true;
                break;
        }
        Text(TitleText, title);
        Text(CodeText, null);
        Text(BodyText, body);
        Text(LineText, null);
        Text(ConsequenceText, consequence);
        Visible(YesButton, false);
        Visible(NoButton, false);
        Visible(AskAgainButton, false);
        Visible(ReplacedJoinButton, join);
        Visible(ReplacedNotMeButton, notMe);
        Actions.Visibility = Of(join || notMe);
        Visible(NoOtherLink, false);
        // O alerta não se recolhe: só «Não fui eu» o esconde.
        Visible(CloseButton, false);
        AutomationProperties.SetName(this, title ?? body ?? string.Empty);
    }

    // P4 e os erros de «Foi eu»; null limpa.
    public void ShowNote(string? text) => Text(NoteText, text);

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
