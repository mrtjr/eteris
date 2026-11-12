namespace Eteris.Browser.Infrastructure;

public static class StartupArgs
{
    private const string UserDataDirFlag = "--user-data-dir=";

    private static readonly HashSet<string> SafeInternalPages = ["history", "settings", "about", "bookmarks", "welcome", "whatsnew", "credits", "privacy", "passwords", "autofill"];

    // Endereço passado ao abrir o Éteris (ex.: um link aberto a partir de outra app ou da jump list).
    // Só http(s) e algumas páginas internas, sempre na raiz e sem parâmetros.
    public static string? InitialUrl(IEnumerable<string> args)
        => args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var uri)
                                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                                        || (uri.Scheme == "eteris" && SafeInternalPages.Contains(uri.Host)
                                            && (uri.AbsolutePath == "/" || a == Browser.SelfTest.Url) && uri.Query.Length == 0)));

    public static bool WantsNewTab(IEnumerable<string> args)
        => args.Any(a => a.Equals("--new-tab", StringComparison.OrdinalIgnoreCase));

    public static bool IsPrivate(IEnumerable<string> args)
        => args.Any(a => a.Equals("--private", StringComparison.OrdinalIgnoreCase)
                         || a.Equals("--incognito", StringComparison.OrdinalIgnoreCase));

    // Perfil alternativo (como no Chrome): dados, cache e instância única separados do perfil normal.
    public static string? UserDataDir(IEnumerable<string> args)
        => args.FirstOrDefault(a => a.StartsWith(UserDataDirFlag, StringComparison.OrdinalIgnoreCase))?[UserDataDirFlag.Length..];

    // O proxy das provas (docs/sandbox.md, «Proxy com o serviço de rede no AppContainer (D9)»): como o motor já não lê os
    // interruptores do Chromium postos na linha de comandos do Éteris (CommandLineArgsDisabled, INF-09), as provas usam
    // --eteris-proxy=<proxy> e --eteris-proxy-pac=<PAC>, só com um perfil de teste (--user-data-dir). Devolve o que o
    // Prepare põe nos CefCommandLineArgs, por esta ordem; sem perfil de teste, ou sem valor, nada.
    public static IReadOnlyList<(string Switch, string Value)> TestProxy(IEnumerable<string> args, bool customProfile)
    {
        if (!customProfile) return [];
        var list = new List<(string, string)>();
        var proxy = ValueOf(args, "--eteris-proxy=");
        if (proxy is { Length: > 0 }) list.Add(("proxy-server", proxy));
        var pac = ValueOf(args, "--eteris-proxy-pac=");
        if (pac is { Length: > 0 }) list.Add(("proxy-pac-url", pac));
        return list;
    }

    private static string? ValueOf(IEnumerable<string> args, string flag)
        => args.FirstOrDefault(a => a.StartsWith(flag, StringComparison.OrdinalIgnoreCase))?[flag.Length..];
}
