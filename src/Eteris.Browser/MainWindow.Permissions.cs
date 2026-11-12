using System.Windows;
using System.Windows.Documents;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    // EmbeddedIn: o site da página de topo quando o pedido vem de uma moldura de outro site (MOT-04). CanRemember: o cartão
    // só oferece «Lembrar» (e só grava) para o que se pode lembrar (PermissionRules.CanRemember). Drop larga o pedido
    // sem o responder em nome do utilizador. Tab é a aba que pediu: o cartão só aparece com ela à vista e sai com ela
    // (null nos sites fixados e no Espreitar, que se mostram sempre); PromptId é o pedido que o Chromium pode retirar.
    private sealed record PendingPermission(string Site, IReadOnlyCollection<SitePermission> Kinds, Action<bool> Respond, Action Drop,
        string? EmbeddedIn, bool CanRemember, string? Detail = null, BrowserTab? Tab = null, ulong? PromptId = null)
    {
        public bool IsLocalNetwork => Kinds.Count > 0 && Kinds.All(k => k == SitePermission.LocalNetwork);
    }

    // Os pedidos à espera, por ordem de chegada (o que está no cartão já saiu daqui), e quando o cartão atual apareceu.
    private readonly List<PendingPermission> _pendingPermissions = [];
    private PendingPermission? _currentPermission;
    private DateTime _permissionShownUtc;
    private SitePermissionStore? _sitePermissions;
    private SitePermissionHandler? _permissionHandler;

    // Na janela privada, as decisões («Lembrar neste site» incluído) ficam só em memória e acabam com a janela: nada
    // vai para as permissões do perfil.
    private SitePermissionStore SitePermissions => _sitePermissions ??= CreatePermissionStore();

    private SitePermissionStore CreatePermissionStore()
    {
        if (!IsPrivate) return new SitePermissionStore(AppPaths.Database);
        var store = SitePermissionStore.InMemory();
        Closed += (_, _) => store.Close();
        return store;
    }

    private SitePermissionHandler PermissionHandler => _permissionHandler ??= new SitePermissionHandler(SitePermissions, ask => AskPermission(ask),
        onCapture: source => Dispatcher.InvokeAsync(() =>
        {
            if (TabOf(source) is { } tab) tab.IsCapturing = true;
        }),
        // O Chromium retirou o pedido (a página navegou, o separador fechou): o cartão sai, sem responder ao motor.
        onDismissed: promptId => Dispatcher.InvokeAsync(() => DropPermissions(p => p.PromptId == promptId, answer: false)));

    // A aba de um motor (a que pede, quando quem chama não a conhece).
    private BrowserTab? TabOf(object? source) => _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, source)).Key;

    // Chega na thread do CEF (ou na da janela, no motor de vídeo). «tab» é a aba que pede quando quem chama a conhece;
    // senão é a do motor de origem (TabOf(ask.Source)).
    private void AskPermission(PermissionAsk ask, BrowserTab? tab = null)
    {
        var site = ask.Site;
        var kinds = ask.Kinds;
        var canRemember = PermissionRules.CanRemember(kinds, ask.EmbeddedIn is not null);
        if (PermissionRules.AutoDeny(kinds, Data.Settings.Current.BlockNotificationPrompts))
        {
            ask.Respond(false);
            return;
        }
        // O pedido do próprio Chromium para a rede local (WebSockets, nomes que apontam para endereços locais):
        // vale o que o utilizador já deixou, incluindo «só desta vez».
        if (kinds.Count > 0 && kinds.All(k => k == SitePermission.LocalNetwork) && LocalNetworkGrants.IsAllowed(site, IsPrivate))
        {
            ask.Respond(true);
            return;
        }
        // «Permitir» sem «Lembrar» desta sessão (notificações, área de transferência, várias transferências): o
        // motor já não guarda a decisão (MOT-03), por isso o Éteris lembra-a até fechar, sem novo cartão. Quando o
        // pedido não se pode lembrar (embutido noutro site, partilha de ecrã) o atalho não vale.
        if (canRemember && kinds.Count > 0 && kinds.All(k => PermissionGrants.IsAllowed(site, k, IsPrivate)))
        {
            ask.Respond(true);
            return;
        }
        Dispatcher.InvokeAsync(() =>
        {
            _pendingPermissions.Add(new PendingPermission(site, kinds, ask.Respond, ask.Drop, ask.EmbeddedIn, canRemember,
                Tab: tab ?? TabOf(ask.Source), PromptId: ask.PromptId));
            ShowNextPermission();
        });
    }

    // Decisões que o Chromium guardou e o Éteris não tem (restos de versões anteriores, ou «Repor» nas Definições, que
    // não tem o motor à mão) voltam ao valor por omissão no commit de cada página, nas abas e nos painéis (MOT-03).
    private static void ResetUnstoredEnginePermissions(CefSharp.IBrowser browser, string url, SitePermissionStore store)
    {
        if (browser.GetHost()?.RequestContext is not { } context) return;
        var site = PermissionRules.Site(url);
        EnginePermissions.Reset(context, url, EnginePermissions.Unstored(k => store.Get(site, k)));
    }

    // O cartão atual é de uma aba que já não está à vista: volta ao início da fila (e a aba, ao voltar, mostra-o de novo).
    // Depois mostra o primeiro pedido à espera cuja aba está à vista.
    private void ShowNextPermission()
    {
        if (_currentPermission is { Tab: { } shownTab } held && !IsTabOnScreen(shownTab))
        {
            _pendingPermissions.Insert(0, held);
            _currentPermission = null;
        }
        if (_currentPermission is not null) return;
        var index = PermissionCard.Next(_pendingPermissions.Select(p => p.Tab is null || IsTabOnScreen(p.Tab)).ToList());
        if (index < 0)
        {
            PermissionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var next = _pendingPermissions[index];
        _pendingPermissions.RemoveAt(index);
        _currentPermission = next;
        _permissionShownUtc = DateTime.UtcNow;
        PermissionText.Inlines.Clear();
        PermissionText.Inlines.Add(new Bold(new Run(next.Site)));
        PermissionText.Inlines.Add(new Run((next.EmbeddedIn is null ? string.Empty : " (dentro de " + next.EmbeddedIn + ")")
            + " quer " + PermissionText_Describe(next.Kinds)));
        // Rede local: «Bloquear», «Só desta vez» e «Permitir sempre» (na janela privada nada fica guardado: sem «sempre»).
        var lan = next.IsLocalNetwork;
        PermissionDetail.Text = next.Detail ?? string.Empty;
        PermissionDetail.Visibility = string.IsNullOrEmpty(next.Detail) ? Visibility.Collapsed : Visibility.Visible;
        PermissionRemember.Visibility = PermissionRememberLabel.Visibility = lan || !next.CanRemember ? Visibility.Collapsed : Visibility.Visible;
        PermissionOnceButton.Visibility = lan ? Visibility.Visible : Visibility.Collapsed;
        PermissionAllowButton.Visibility = lan && IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        PermissionAllowButton.Content = lan ? "Permitir sempre" : "Permitir";
        PermissionGlyph.Text = lan ? "" : next.Kinds.Order().FirstOrDefault() switch
        {
            SitePermission.Camera => "",
            SitePermission.Microphone => "",
            SitePermission.ScreenShare => "",
            SitePermission.Location => "",
            SitePermission.Notifications => "",
            SitePermission.Clipboard => "",
            _ => "",
        };
        // Câmara, microfone, ecrã e localização: «Lembrar» desligado; o utilizador marca-o se quiser.
        PermissionRemember.IsChecked = PermissionCard.RemembersByDefault(next.Kinds);
        // Na janela privada a decisão fica só em memória, até a janela fechar.
        PermissionRememberLabel.Text = IsPrivate ? "Lembrar nesta janela" : "Lembrar neste site";
        PermissionBar.Visibility = Visibility.Visible;
    }

    private static string PermissionText_Describe(IReadOnlyCollection<SitePermission> kinds)
        => Permissions.PermissionText.Describe(kinds) + ".";

    private void AnswerPermission(bool allowed, bool once = false)
    {
        if (_currentPermission is not { } current) return;
        _currentPermission = null;
        if (current.IsLocalNetwork)
        {
            // «Bloquear» não fica guardado (o cartão não volta a aparecer nesta janela para o site); «Só desta vez» vale
            // até fechar o Éteris; «Permitir sempre» fica nas permissões do site (e aparece no cadeado).
            if (allowed && (once || IsPrivate)) LocalNetworkGrants.Allow(current.Site, IsPrivate);
            else if (allowed) SitePermissions.Set(current.Site, SitePermission.LocalNetwork, true);
        }
        else if (current.CanRemember && PermissionRemember.IsChecked == true)
            foreach (var kind in current.Kinds) SitePermissions.Set(current.Site, kind, allowed);
        else if (allowed && current.CanRemember)
            // Sem «Lembrar»: vale até fechar o Éteris para os tipos de PermissionGrants.Applies; os outros voltam a
            // perguntar no pedido seguinte. «Bloquear» sem «Lembrar» não fica em lado nenhum.
            foreach (var kind in current.Kinds) PermissionGrants.Allow(current.Site, kind, IsPrivate);
        current.Respond(allowed);
        ShowNextPermission();
    }

    // Tira da fila (e do cartão) os pedidos que «which» escolhe: a aba fechou, adormeceu ou navegou, ou o Chromium retirou
    // o pedido. Com «answer», o motor é avisado de que o pedido caiu (Drop); sem ele, já não há a quem avisar.
    private void DropPermissions(Func<PendingPermission, bool> which, bool answer = true)
    {
        var dropped = new List<PendingPermission>();
        _pendingPermissions.RemoveAll(p =>
        {
            if (!which(p)) return false;
            dropped.Add(p);
            return true;
        });
        if (_currentPermission is { } current && which(current))
        {
            dropped.Add(current);
            _currentPermission = null;
        }
        if (dropped.Count == 0) return;
        if (answer)
            foreach (var p in dropped) p.Drop();
        ShowNextPermission();
    }

    // O instante a partir do qual o cartão aceita cliques: o mais recente entre a ativação da janela principal e a do
    // Éteris (o cartão vive na janela da camada: voltar de outra aplicação e clicar nele não ativa a principal).
    private DateTime LastActivationUtc => _appActivatedUtc > _lastActivatedUtc ? _appActivatedUtc : _lastActivatedUtc;

    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        if (!PermissionCard.AcceptsClick(_permissionShownUtc, LastActivationUtc, DateTime.UtcNow)) return;
        AnswerPermission(true);
    }

    private void PermissionOnce_Click(object sender, RoutedEventArgs e)
    {
        if (!PermissionCard.AcceptsClick(_permissionShownUtc, LastActivationUtc, DateTime.UtcNow)) return;
        AnswerPermission(true, once: true);
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        if (!PermissionCard.AcceptsClick(_permissionShownUtc, LastActivationUtc, DateTime.UtcNow)) return;
        AnswerPermission(false);
    }
}
