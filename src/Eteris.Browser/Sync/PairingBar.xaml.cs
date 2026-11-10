using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Eteris.Browser.Sync;

// A barra de aprovação (0.2.15): desenha uma PairingBarView (ou o conselho depois de uma recusa) e levanta eventos. Não
// conhece a sincronização, não se mostra nem se esconde a si própria (isso é do ponto único da janela, RefreshBars) e
// nunca pede o foco do teclado: o Enter só conta dentro da caixa do código, onde a pessoa clicou.
internal sealed partial class PairingBar : UserControl
{
    private PairingBarView? _view;
    private bool _advice;

    public PairingBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(CodeBox, "Código que o outro computador mostra");
        // Os leitores de ecrã anunciam o que muda (a barra aparece sem levar o foco).
        AutomationProperties.SetLiveSetting(TitleText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(NoteText, AutomationLiveSetting.Polite);
        // Só algarismos e espaços (também ao colar).
        CodeBox.PreviewTextInput += (_, e) => e.Handled = !Accepts(e.Text);
        DataObject.AddPastingHandler(CodeBox, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !Accepts(text)) e.CancelCommand();
        });
        // O Enter só é tratado aqui, na caixa: «o código é a prova». Também quando já vem tratado (a caixa de texto
        // pode marcá-lo), para a tecla nunca se perder.
        CodeBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnBoxKeyDown), handledEventsToo: true);
        // A camada não está ativa: com um clique na caixa a janela pode ter de ativar a camada para ela receber teclas.
        CodeBox.PreviewMouseLeftButtonDown += (_, _) => BoxPressed?.Invoke();
        Wire(NotMeButton, () => { if (_view?.Session is { } session) NotMe?.Invoke(session); });
        Wire(LookButton, () => LookRequested?.Invoke());
        Wire(RevokeButton, () => RevokeRequested?.Invoke());
        Wire(CloseButton, () => AdviceClosed?.Invoke());
    }

    public event Action<string, string>? CodeEntered;   // (sessão, texto escrito)
    public event Action<string>? NotMe;                 // «Não fui eu» (sessão)
    public event Action? LookRequested;                 // «Ver»
    public event Action? RevokeRequested;               // «Retirar o acesso do Éteris»
    public event Action? AdviceClosed;                  // «Fechar», por baixo do conselho
    public event Action? BoxPressed;                    // clique na caixa do código

    // A caixa do código tem o foco do teclado (para a janela o devolver à página quando a barra sai).
    public bool BoxHasFocus => CodeBox.IsKeyboardFocusWithin;

    // A sessão cujo código se está a pedir (null fora do estado Code e com o conselho à vista).
    public string? ShownSession => _advice || _view is not { Kind: PairingBarKind.Code } ? null : _view.Session;

    // O que a caixa aceita escrever.
    internal static bool Accepts(string text) => text.All(c => c is ' ' || c is >= '0' and <= '9');

    // Desenha a vista. A mesma sessão outra vez (RefreshBars corre a cada mudança da sincronização) não limpa nada:
    // nem o que se está a escrever nem o aviso do último código.
    public void Show(PairingBarView view)
    {
        var sameRequest = !_advice && _view is { Kind: PairingBarKind.Code } last && view.Kind == PairingBarKind.Code && last.Session == view.Session;
        if (!sameRequest)
        {
            CodeBox.Clear();
            Note(null);
        }
        _advice = false;
        _view = view;
        var code = view.Kind == PairingBarKind.Code;
        Title(view.Kind switch
        {
            PairingBarKind.Code => view.Title + ".",
            PairingBarKind.Connecting => PairingBarModel.Connecting,
            PairingBarKind.Waiting => PairingBarModel.Waiting,
            _ => string.Empty,
        });
        AskText.Text = PairingBarModel.Ask;
        AskText.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        NameText.Text = code && view.Name is { } name ? PairingBarModel.NameLine(name) : string.Empty;
        NameText.Visibility = code && view.Name is not null ? Visibility.Visible : Visibility.Collapsed;
        Buttons(box: code, notMe: code, look: view.Kind == PairingBarKind.Waiting, revoke: false, close: false);
    }

    // Um aviso dentro do estado Code (código errado, porta do Windows recusada, erro ao entregar). Limpa-se na tentativa
    // seguinte ou quando a sessão muda.
    public void ShowNote(string text) => Note(text);

    // O conselho depois de «Não fui eu» ou do 3.º código errado, com «Retirar o acesso do Éteris» e «Fechar». Fica até
    // um dos dois (não some sozinho: é o único aviso de que alguém tem a conta Google).
    public void ShowAdvice(string text)
    {
        _advice = true;
        CodeBox.Clear();
        Note(null);
        Title(text);
        AskText.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Collapsed;
        Buttons(box: false, notMe: false, look: false, revoke: true, close: true);
    }

    private void Title(string text)
    {
        if (TitleText.Text == text) return;
        TitleText.Text = text;
        AutomationProperties.SetName(this, text);
    }

    private void Note(string? text)
    {
        NoteText.Text = text ?? string.Empty;
        NoteText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Buttons(bool box, bool notMe, bool look, bool revoke, bool close)
    {
        static Visibility Of(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        CodeFrame.Visibility = Of(box);
        NotMeButton.Visibility = Of(notMe);
        LookButton.Visibility = Of(look);
        RevokeButton.Visibility = Of(revoke);
        CloseButton.Visibility = Of(close);
        Actions.Visibility = Of(box || notMe || look || revoke || close);
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _advice || _view is not { Kind: PairingBarKind.Code, Session: { } session }) return;
        e.Handled = true;
        var typed = CodeBox.Text;
        // Cada tentativa começa limpa (o aviso da anterior sai, a caixa esvazia-se).
        CodeBox.Clear();
        Note(null);
        CodeEntered?.Invoke(session, typed);
    }

    // Ao premir (não ao largar), como na barra do início de sessão: a camada não está ativa e o primeiro clique numa
    // janela inativa pode perder o «largar». Pela automação (leitor de ecrã), o Click normal.
    private static void Wire(Button button, Action action)
    {
        AutomationProperties.SetName(button, (string)button.Content);
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        button.Click += (_, _) => action();
    }
}
