namespace Eteris.Browser.Infrastructure;

public static class StartupArgs
{
    // Endereço passado ao abrir o Éteris (ex.: um link aberto a partir de outra app). Só http(s).
    public static string? InitialUrl(IEnumerable<string> args)
        => args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var uri)
                                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
