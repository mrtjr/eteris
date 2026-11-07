using System.Windows;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

public partial class MainWindow
{
    private IRequestContext? _privateContext;

    public bool IsPrivate { get; }

    private string NewTabUrl => IsPrivate ? NewTabPage.PrivateUrl : NewTabPage.Url;

    // Contexto sem CachePath: cookies, cache e armazenamento ficam só em memória e acabam com a janela.
    private IRequestContext? PrivateContext => IsPrivate
        ? _privateContext ??= CreatePrivateContext()
        : null;

    private static IRequestContext CreatePrivateContext()
    {
        // Uma só língua para os sites também aqui (o contexto privado não herda a lista do perfil): a mesma com que o
        // motor arrancou, que é a que vai no Accept-Language de todos os pedidos — a das Definições só vale depois de
        // reiniciar, e navigator.languages diferente do cabeçalho distinguia quem a mudou.
        var language = Shields.EngineHardening.StartedWithLanguage is { Length: > 0 } started ? started : Shields.PrivacyPreferences.SiteLanguage;
        var context = new RequestContext(new RequestContextSettings { AcceptLanguageList = language });
        // As fábricas de esquemas são por contexto: sem isto eteris:// dá ERR_UNKNOWN_URL_SCHEME na janela privada.
        context.RegisterSchemeHandlerFactory(EterisSchemeHandlerFactory.SchemeName, string.Empty, new EterisSchemeHandlerFactory(privateContext: true));
        SelfTestSchemeHandlerFactory.Register(context);
        _ = Shields.PrivacyPreferences.ApplyAsync(context);
        return context;
    }

    private void ConfigurePrivateWindow()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x0B, 0x24));
        PrivateBadge.Visibility = Visibility.Visible;
        ShowCaptureState();
        void OnSettings(Data.UserSettings _) => Dispatcher.InvokeAsync(ShowCaptureState);
        Data.Settings.Changed += OnSettings;
        Closed += (_, _) => Data.Settings.Changed -= OnSettings;
        Closed += (_, _) =>
        {
            _privateContext?.Dispose();
            // A última janela privada leva consigo as autorizações de http:// dadas na sessão privada.
            if (!PrivateWindows().Any(w => !ReferenceEquals(w, this)))
            {
                Shields.ShieldEngine.Https.ForgetPrivate();
                Shields.LocalNetworkGrants.ForgetPrivate();
                Shields.SiteRules.ForgetPrivate();
                Shields.Cname.PrivateCache.Clear();
            }
        };
    }

    private bool _endingPrivateSession;

    // O selo diz se as janelas estão escondidas das capturas e do Recall (olho riscado) e explica o clique.
    private void ShowCaptureState()
    {
        var hidden = Data.Settings.Current.HideFromCapture;
        PrivateCaptureGlyph.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        PrivateBadge.ToolTip = Infrastructure.CaptureText.BadgeTip(hidden);
    }

    private void PrivateBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleHideFromCapture();
    }

    // Selo «Privada», paleta e Definições › Privacidade: o aviso diz o que mudou e deixa anular.
    private void ToggleHideFromCapture()
    {
        var hide = !Data.Settings.Current.HideFromCapture;
        Data.Settings.Set("hideFromCapture", hide ? "true" : "false");
        ShowUndoToast(Infrastructure.CaptureText.Toast(hide), hide ? "Esconder das capturas" : "Mostrar nas capturas",
            () => Data.Settings.Set("hideFromCapture", hide ? "false" : "true"));
    }

    private static IReadOnlyList<MainWindow> PrivateWindows()
        => Application.Current?.Windows.OfType<MainWindow>().Where(w => w.IsPrivate && w.IsLoaded).ToList() ?? [];

    // «Terminar sessão privada» (menu do Éteris e paleta): primeiro diz o que vai fechar; «Terminar» fecha todas as
    // janelas privadas — cada uma descarta o seu contexto em memória — sem perguntar pelas abas trancadas.
    private void OfferEndPrivateSession()
    {
        var windows = PrivateWindows();
        if (windows.Count == 0)
        {
            ShowToast("Não há nenhuma janela privada aberta");
            return;
        }
        var tabs = windows.Sum(w => w.Tabs.Tabs.Count(t => !NewTabPage.IsNewTab(t.Address)));
        ShowToast(Eteris.Browser.Tabs.PrivateSession.Confirmation(windows.Count, tabs), new ToastAction("Terminar", EndPrivateSession));
    }

    // Depois do clique (o botão do aviso pode estar numa das janelas que vão fechar).
    private void EndPrivateSession() => Dispatcher.BeginInvoke(() =>
    {
        var windows = PrivateWindows();
        foreach (var window in windows)
        {
            window._endingPrivateSession = true;
            window.Close();
        }
        Shields.ShieldEngine.Https.ForgetPrivate();
        Shields.LocalNetworkGrants.ForgetPrivate();
        Shields.SiteRules.ForgetPrivate();
        Shields.Cname.PrivateCache.Clear();
        // O aviso do fim aparece numa janela normal que fique.
        var survivor = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => !w.IsPrivate);
        survivor?.ShowToast(Eteris.Browser.Tabs.PrivateSession.Done(windows.Count));
    });

    internal static void OpenWindow(bool isPrivate, string? url = null)
    {
        var window = new MainWindow(url, isPrivate);
        window.Show();
        window.Activate();
    }
}
