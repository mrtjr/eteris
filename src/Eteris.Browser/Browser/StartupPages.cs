namespace Eteris.Browser.Browser;

public sealed record StartupTab(string Url, bool Activate);

// Que abas abrir ao arrancar, além da sessão restaurada.
public static class StartupPages
{
    // Primeiro arranque: boas-vindas (uma vez), mesmo que o Éteris tenha sido aberto com um endereço — esse endereço
    // abre também e fica à frente. Depois de uma atualização: «Novidades» uma vez por versão.
    public static IReadOnlyList<StartupTab> Plan(bool firstRun, string lastSeenVersion, string version, string? initialUrl)
    {
        var tabs = new List<StartupTab>();
        if (initialUrl is not null) tabs.Add(new StartupTab(initialUrl, Activate: true));
        if (firstRun)
            tabs.Add(new StartupTab(WelcomePage.Url, Activate: initialUrl is null));
        else if (WhatsNewPage.ShouldShow(lastSeenVersion, version, firstRun: false))
            tabs.Add(new StartupTab(WhatsNewPage.Url, Activate: true));
        return tabs;
    }
}
