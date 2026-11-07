namespace Eteris.Browser.Browser;

public sealed record StartupTab(string Url, bool Activate);

// Que abas abrir ao arrancar, além da sessão restaurada.
public static class StartupPages
{
    // Primeiro arranque: boas-vindas (uma vez), mesmo que o Éteris tenha sido aberto com um endereço — esse endereço
    // abre também e fica à frente. Depois de uma atualização: «Novidades» uma vez por versão. Sem nada para mostrar
    // nem abas retomadas da sessão (hasRestoredTabs), abre uma nova aba: uma janela nunca arranca vazia.
    public static IReadOnlyList<StartupTab> Plan(bool firstRun, string lastSeenVersion, string version, string? initialUrl,
        bool hasRestoredTabs = true)
    {
        var tabs = new List<StartupTab>();
        if (initialUrl is not null) tabs.Add(new StartupTab(initialUrl, Activate: true));
        if (firstRun)
            tabs.Add(new StartupTab(WelcomePage.Url, Activate: initialUrl is null));
        else if (WhatsNewPage.ShouldShow(lastSeenVersion, version, firstRun: false))
            tabs.Add(new StartupTab(WhatsNewPage.Url, Activate: true));
        if (tabs.Count == 0 && !hasRestoredTabs) tabs.Add(new StartupTab(NewTabPage.Url, Activate: true));
        return tabs;
    }

    // O plano só corre na janela dona da sessão e já inclui o endereço passado ao abrir: abri-lo outra vez dava duas
    // abas iguais (duas páginas a carregar, dois pedidos de tudo). As outras janelas (segunda janela, privada) não têm
    // plano e abrem o endereço elas mesmas.
    public static bool OpensInitialUrlSeparately(bool ownsSession, string? initialUrl) => !ownsSession && initialUrl is not null;
}
