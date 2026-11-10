using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Eteris.Browser.Sync;

// A barra do início de sessão (e, nas tarefas seguintes, da sessão expirada e do lembrete do kit, que partilham este
// controlo): mostra uma SignInBarView e levanta Acted com a ação do botão premido. Não guarda estado além da última
// vista, não decide nada e não se mostra nem se esconde a si própria (isso é do ponto único da janela, RefreshBars).
internal sealed partial class SignInBar : UserControl
{
    private SignInBarView? _shown;

    // A frase (com o nome a negrito) e os botões. Criados aqui e não no XAML: vivem num painel próprio que decide se
    // cabem numa linha (BarLayout).
    internal readonly TextBlock Message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };

    internal readonly WrapPanel Actions = new() { Orientation = Orientation.Horizontal };

    public SignInBar()
    {
        InitializeComponent();
        // Os leitores de ecrã anunciam a frase quando muda (a barra aparece sem levar o foco).
        AutomationProperties.SetLiveSetting(Message, AutomationLiveSetting.Polite);
        Body.Child = new BarLayout { Children = { Message, Actions } };
    }

    public event Action<SignInBarAction>? Acted;

    // Preenche o texto e os botões. A mesma vista outra vez (RefreshBars corre a cada evento da sincronização) não refaz
    // nada: a barra não pisca nem se repete no leitor de ecrã.
    public void Show(SignInBarView view)
    {
        if (_shown is { } last && Same(last, view)) return;
        _shown = view;

        Message.Inlines.Clear();
        var at = view.Bold is { Length: > 0 } bold ? view.Text.IndexOf(bold, StringComparison.Ordinal) : -1;
        if (at < 0) Message.Inlines.Add(new Run(view.Text));
        else
        {
            if (at > 0) Message.Inlines.Add(new Run(view.Text[..at]));
            Message.Inlines.Add(new Bold(new Run(view.Bold)));
            var after = at + view.Bold!.Length;
            if (after < view.Text.Length) Message.Inlines.Add(new Run(view.Text[after..]));
        }
        AutomationProperties.SetName(this, view.Text);

        Actions.Children.Clear();
        foreach (var button in view.Buttons) Actions.Children.Add(NewButton(button));
        Announce();
    }

    private static bool Same(SignInBarView a, SignInBarView b)
        => a.Kind == b.Kind && a.Text == b.Text && a.Bold == b.Bold && a.Buttons.SequenceEqual(b.Buttons);

    private Button NewButton(SignInBarButton spec)
    {
        Button button;
        if (spec.Action == SignInBarAction.Close)
        {
            // «Fechar» é um botão de ícone discreto (×), como o da bolha de guardar a palavra-passe.
            button = new Button { Content = "", FontSize = 10, Width = 26, Height = 26, ToolTip = spec.Label, Margin = new Thickness(6, 0, -4, 0) };
            button.SetResourceReference(StyleProperty, "IconButton");
            button.SetResourceReference(FontFamilyProperty, "IconFont");
        }
        else
        {
            button = new Button { Content = spec.Label, Margin = new Thickness(8, 2, 0, 2) };
            button.SetResourceReference(StyleProperty, spec.Primary ? "PrimaryPillButton" : "PillButton");
        }
        AutomationProperties.SetName(button, spec.Label);
        var action = spec.Action;
        // Ao premir (não ao largar), como no aviso curto: a camada não está ativa e o primeiro clique numa janela
        // inativa pode perder o «largar» para a ativação.
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Acted?.Invoke(action);
        };
        // Pelo teclado ou pela automação (leitor de ecrã), o Click normal.
        button.Click += (_, _) => Acted?.Invoke(action);
        return button;
    }

    private void Announce()
    {
        try { UIElementAutomationPeer.FromElement(Message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); }
        catch (InvalidOperationException) { }
    }

    // Uma linha (frase à esquerda, botões à direita) quando cabe; senão a frase parte linha à largura disponível e os
    // botões vão para baixo, encostados à direita (e também partem linha entre si).
    private sealed class BarLayout : Panel
    {
        private const double Gap = 12;
        private const double RowGap = 8;
        private bool _oneLine = true;

        protected override Size MeasureOverride(Size available)
        {
            var text = InternalChildren[0];
            var buttons = InternalChildren[1];
            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            text.Measure(infinite);
            buttons.Measure(infinite);
            var hasButtons = ((Panel)buttons).Children.Count > 0;
            var line = text.DesiredSize.Width + (hasButtons ? Gap + buttons.DesiredSize.Width : 0);
            _oneLine = line <= available.Width;
            if (_oneLine)
                return new Size(line, Math.Max(text.DesiredSize.Height, buttons.DesiredSize.Height));
            var width = new Size(available.Width, double.PositiveInfinity);
            text.Measure(width);
            buttons.Measure(width);
            return hasButtons
                ? new Size(Math.Max(text.DesiredSize.Width, buttons.DesiredSize.Width), text.DesiredSize.Height + RowGap + buttons.DesiredSize.Height)
                : text.DesiredSize;
        }

        protected override Size ArrangeOverride(Size final)
        {
            var text = InternalChildren[0];
            var buttons = InternalChildren[1];
            if (_oneLine)
            {
                text.Arrange(new Rect(0, (final.Height - text.DesiredSize.Height) / 2, text.DesiredSize.Width, text.DesiredSize.Height));
                var bw = buttons.DesiredSize.Width;
                buttons.Arrange(new Rect(Math.Max(0, final.Width - bw), (final.Height - buttons.DesiredSize.Height) / 2, bw, buttons.DesiredSize.Height));
            }
            else
            {
                text.Arrange(new Rect(0, 0, final.Width, text.DesiredSize.Height));
                var bw = Math.Min(buttons.DesiredSize.Width, final.Width);
                buttons.Arrange(new Rect(final.Width - bw, text.DesiredSize.Height + RowGap, bw, buttons.DesiredSize.Height));
            }
            return final;
        }
    }
}
