namespace Eteris.Browser.Infrastructure;

public static class StartupArgs
{
    private const string UserDataDirFlag = "--user-data-dir=";

    private static readonly HashSet<string> SafeInternalPages = ["history", "settings", "about", "bookmarks", "welcome", "whatsnew", "credits", "privacy"];

    // Endereço passado ao abrir o Éteris (ex.: um link aberto a partir de outra app ou da jump list).
    // Só http(s) e algumas páginas internas, sempre na raiz e sem parâmetros.
    public static string? InitialUrl(IEnumerable<string> args)
        => args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var uri)
                                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                                        || (uri.Scheme == "eteris" && SafeInternalPages.Contains(uri.Host)
                                            && uri.AbsolutePath == "/" && uri.Query.Length == 0)));

    public static bool WantsNewTab(IEnumerable<string> args)
        => args.Any(a => a.Equals("--new-tab", StringComparison.OrdinalIgnoreCase));

    public static bool IsPrivate(IEnumerable<string> args)
        => args.Any(a => a.Equals("--private", StringComparison.OrdinalIgnoreCase)
                         || a.Equals("--incognito", StringComparison.OrdinalIgnoreCase));

    // Perfil alternativo (como no Chrome): dados, cache e instância única separados do perfil normal.
    public static string? UserDataDir(IEnumerable<string> args)
        => args.FirstOrDefault(a => a.StartsWith(UserDataDirFlag, StringComparison.OrdinalIgnoreCase))?[UserDataDirFlag.Length..];
}
