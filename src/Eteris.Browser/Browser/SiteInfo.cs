namespace Eteris.Browser.Browser;

public enum ConnectionKind { Secure, Insecure, Local, Internal, File, Unknown }

public sealed record SiteInfoResult(ConnectionKind Kind, string Host, string Origin, string Title, string Detail);

// Texto do painel de informação do site (cadeado na omnibox).
public static class SiteInfo
{
    public static SiteInfoResult Describe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new(ConnectionKind.Unknown, url, url, "Endereço desconhecido", string.Empty);

        var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        return uri.Scheme switch
        {
            "eteris" => new(ConnectionKind.Internal, uri.Host, origin, "Página do Éteris",
                "Esta página faz parte do navegador e não comunica com nenhum site."),
            "file" => new(ConnectionKind.File, "Ficheiro local", "file://", "Ficheiro no seu computador",
                "Está a ver um ficheiro guardado neste computador."),
            "https" => new(ConnectionKind.Secure, uri.Host, origin, "Ligação segura",
                "As informações que envia (palavras-passe, cartões) seguem encriptadas até este site."),
            "http" when uri.IsLoopback => new(ConnectionKind.Local, uri.Host, origin, "Servidor local",
                "Este site corre no seu próprio computador."),
            "http" => new(ConnectionKind.Insecure, uri.Host, origin, "Ligação não segura",
                "Não introduza palavras-passe nem dados de pagamento: podem ser lidos por terceiros na rede."),
            _ => new(ConnectionKind.Unknown, uri.Host, origin, "Endereço desconhecido", string.Empty),
        };
    }
}
