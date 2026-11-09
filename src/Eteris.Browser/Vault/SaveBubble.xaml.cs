using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Vault;

// A bolha de guardar ou atualizar uma palavra-passe (docs/palavras-passe.md, C4): o utilizador pode corrigir o
// utilizador; a palavra-passe aparece escondida e o olho mostra-a sem pedir o Windows (acabou de a escrever), com a
// janela fora das capturas enquanto estiver à vista. «Guardar», «Agora não», ⋯ › «Nunca neste site»; Esc e × são
// «Agora não».
public partial class SaveBubble : UserControl
{
    private const string Mask = "••••••••••";
    private string _password = string.Empty;
    private IDisposable? _onScreen;

    public SaveBubble()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => SaveRequested?.Invoke(UserBox.Text.Trim());
        NotNowButton.Click += (_, _) => NotNowRequested?.Invoke();
        CloseButton.Click += (_, _) => NotNowRequested?.Invoke();
        EyeButton.Click += (_, _) => ShowPassword(_onScreen is null);
        MoreButton.Click += (_, _) => new DarkMenu()
            .Item("", "Nunca neste site", () => NeverRequested?.Invoke())
            .OpenBelow(MoreButton);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                NotNowRequested?.Invoke();
            }
            else if (e.Key == Key.Enter && UserBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                SaveRequested?.Invoke(UserBox.Text.Trim());
            }
        };
    }

    // O nome do utilizador (corrigido ou não).
    public event Action<string>? SaveRequested;

    public event Action? NotNowRequested;

    public event Action? NeverRequested;

    public void Show(SaveOffer offer)
    {
        Question.Text = SavePolicy.Question(offer);
        UserBox.Text = offer.Username;
        SaveButton.Content = offer.Kind == SaveOfferKind.Update ? "Atualizar" : "Guardar";
        _password = offer.Password;
        ShowPassword(false);
    }

    // O teclado vai para a bolha (o utilizador pediu: F6 ou o ícone de chave): para o utilizador, que se pode corrigir.
    public void FocusFirst()
    {
        UserBox.Focus();
        UserBox.SelectAll();
    }

    // Diz a pergunta ao leitor de ecrã (a bolha apareceu sozinha, sem levar o foco).
    public void Announce(string text)
    {
        try
        {
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
                System.Windows.Automation.AutomationNotificationKind.Other, System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                text, "eteris-guardar-palavra-passe");
        }
        catch (InvalidOperationException) { }
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new BubblePeer(this);

    // A bolha é um «painel» com nome para o leitor de ecrã (como as do Chrome, Firefox e Brave).
    private sealed class BubblePeer(SaveBubble owner) : System.Windows.Automation.Peers.UserControlAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.Pane;
        protected override string GetClassNameCore() => "BolhaGuardarPalavraPasse";
        protected override string GetNameCore() => owner.Question.Text;
    }

    // A bolha fechou: a palavra-passe sai daqui e a janela volta às capturas.
    public void Forget()
    {
        ShowPassword(false);
        _password = string.Empty;
        UserBox.Text = string.Empty;
    }

    private void ShowPassword(bool show)
    {
        _onScreen?.Dispose();
        _onScreen = null;
        if (show && _password.Length > 0)
        {
            _onScreen = SecretOnScreen.Show(() => Dispatcher.InvokeAsync(() => ShowPassword(false)));
            PasswordText.Text = _password;
        }
        else PasswordText.Text = _password.Length > 0 ? Mask : string.Empty;
        EyeButton.Content = _onScreen is null ? "" : "";
        var tip = _onScreen is null ? "Mostrar a palavra-passe" : "Esconder a palavra-passe";
        EyeButton.ToolTip = tip;
        AutomationProperties.SetName(EyeButton, tip);
        AutomationProperties.SetName(PasswordText, _onScreen is null ? "Palavra-passe escondida" : "Palavra-passe à vista");
    }
}
