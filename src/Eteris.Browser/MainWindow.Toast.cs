using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private void ShowLinkStatus(BrowserTab tab, string? text)
    {
        if (!ReferenceEquals(tab, Tabs.Active) || text is null)
        {
            LinkStatusBubble.Visibility = Visibility.Collapsed;
            SyncLinkBubble(show: false);
            return;
        }
        LinkStatusText.Text = text;
        LinkStatusBubble.Visibility = Visibility.Visible;
        // Ajuste e5: na janela própria da bolha (MainWindow.LinkBubble.cs); sem ele, na camada.
        SyncLinkBubble(show: true);
    }

    private bool _toastWired;

    // O aviso do CrashLog.Install (um erro da interface de que o Éteris recuperou); chamado no fio da interface.
    internal void ShowCrashToast(string message) => ShowToast(message);

    // Aviso curto no fundo da janela: aparece, fica ~1,9 s e desvanece. Com ações («Anular», «Abrir», «Mostrar na
    // pasta», até duas) fica 6 s, recebe o rato e não desaparece enquanto o rato estiver por cima.
    private void ShowToast(string message, params ToastAction[] actions)
    {
        // Na Pausa os avisos ficam em fila e aparecem ao voltar (MainWindow.Pause.cs).
        if (_pause is not null)
        {
            QueuePausedToast(message, actions);
            return;
        }
        ShowToastFor(ToastPolicy.Hold(Math.Min(actions.Length, ToastPolicy.MaxActions)), message, actions);
    }

    // O aparecer e o desvanecer do aviso (FadeToast), que contam no tempo à vista.
    private static readonly TimeSpan ToastFades = TimeSpan.FromMilliseconds(160 + 500);

    // Com «hold»: quanto tempo fica à vista (sem contar o aparecer e o desvanecer).
    private void ShowToastFor(TimeSpan hold, string message, ToastAction[] actions)
    {
        WireToast();
        ToastText.Text = message;
        // O nome que o regresso da Pausa deu ao texto para o leitor de ecrã (MainWindow.Pause.cs) não passa a outro aviso.
        ToastText.ClearValue(AutomationProperties.NameProperty);
        ToastActions.Children.Clear();
        foreach (var action in actions.Take(ToastPolicy.MaxActions))
        {
            var run = action.Run;
            var button = new Button { Content = action.Label, Style = (Style)FindResource("PillButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            // Ao premir (não ao largar): a camada não está ativa e o primeiro clique numa janela inativa pode perder
            // o «largar» para a ativação — o «Anular» não pode falhar à primeira.
            var ran = false;
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (ran) return;
                ran = true;
                HideToastNow();
                run();
                // O clique ativou a camada: o teclado volta para a página.
                ReturnFocusToPage();
            };
            // O Invoke do leitor de ecrã (UI Automation) chega pelo Click; o rato já correu a ação ao premir (e o Click do
            // rato nem chega: o premir foi consumido). Uma só vez, e só com o aviso à vista.
            button.Click += (_, _) =>
            {
                if (ran || !Toast.IsHitTestVisible) return;
                ran = true;
                HideToastNow();
                run();
                ReturnFocusToPage();
            };
            ToastActions.Children.Add(button);
        }
        ToastActions.Visibility = ToastActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Toast.IsHitTestVisible = ToastActions.Children.Count > 0;
        FadeToast(hold, fadeIn: true);
    }

    // Avisos sobre ficheiros do perfil (sessão por gravar, ficheiro danificado…): um ShowToast simples dura 1,9 s e o
    // seguinte substitui-o, por isso juntam-se numa fila e aparecem todos de uma vez, com «Entendi» (fica 6 s e não
    // foge com o rato por cima), na primeira janela normal carregada. Um aviso pedido antes de haver janela aparece
    // quando ela carrega (StartTabs). Só no fio da interface.
    private static readonly List<string> s_profileNotices = [];

    internal static void NotifyProfile(string message)
    {
        if (!s_profileNotices.Contains(message)) s_profileNotices.Add(message);
        ShowProfileNotices();
    }

    // A base de dados do perfil estava danificada e pôs-se de lado (Data/ProfileDatabase.cs): pode vir da abertura em fundo,
    // antes de haver janela, e o aviso espera por ela.
    internal static void NotifyDatabaseSetAside(string fileName)
        => NotifyProfile($"A base de dados do perfil estava danificada: ficou guardada como {fileName} e começou uma nova (favoritos, histórico e escolhas por site vazios)");

    private static void ShowProfileNotices()
    {
        if (s_profileNotices.Count == 0 || NormalWindows().FirstOrDefault(w => w.IsLoaded) is not { } window) return;
        window.ShowToast(string.Join("\n", s_profileNotices), new ToastAction("Entendi", () => { }));
        s_profileNotices.Clear();
    }

    private void FadeToast(TimeSpan hold, bool fadeIn)
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        var start = fadeIn ? TimeSpan.FromMilliseconds(160) : TimeSpan.Zero;
        if (fadeIn) fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(start)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(start + hold)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(start + hold + TimeSpan.FromMilliseconds(500))));
        // Invisível, o aviso deixa de apanhar cliques (a camada continua por cima da página com outras coisas), e os botões
        // saem da árvore de automação e da ordem do Tab (um «Anular» invisível reabria uma aba minutos depois).
        fade.Completed += (_, _) =>
        {
            if (Toast.Opacity < 0.01)
            {
                Toast.IsHitTestVisible = false;
                ToastActions.Visibility = Visibility.Collapsed;
            }
        };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void HideToastNow()
    {
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 0;
        Toast.IsHitTestVisible = false;
        ToastActions.Visibility = Visibility.Collapsed;
    }

    // Com o rato por cima o aviso fica; ao sair, ainda dá dois segundos para voltar.
    private void WireToast()
    {
        if (_toastWired) return;
        _toastWired = true;
        Toast.MouseEnter += (_, _) =>
        {
            if (!Toast.IsHitTestVisible) return;
            Toast.BeginAnimation(OpacityProperty, null);
            Toast.Opacity = 1;
        };
        Toast.MouseLeave += (_, _) =>
        {
            if (Toast.IsHitTestVisible) FadeToast(TimeSpan.FromSeconds(2), fadeIn: false);
        };
    }
}
