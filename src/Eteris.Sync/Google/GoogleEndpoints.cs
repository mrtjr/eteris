using System.Net;

namespace Eteris.Sync.Google;

// EXCEÇÃO DOCUMENTADA à regra «os pedidos de rede do Éteris passam só pelo motor» (docs/sincronizacao.md, «A exceção
// de rede»): a sincronização pelo Google Drive fala com a Google por um cliente HTTP próprio, e SÓ com isto:
//
//   accounts.google.com/o/oauth2/v2/auth       — aberto no NAVEGADOR DO SISTEMA (nunca pedido por este cliente)
//   oauth2.googleapis.com/token, /revoke       — trocar o código por tokens, renovar e revogar
//   www.googleapis.com/drive/v3/…              — listar, ler e apagar na pasta oculta da aplicação (appDataFolder)
//   www.googleapis.com/upload/drive/v3/…       — enviar ficheiros (multipart e resumable)
//   www.googleapis.com/drive/v3/about          — nome e e-mail da conta (fields=user(displayName,emailAddress)),
//                                                só depois de uma ação do utilizador (GoogleAccountService)
//
// Só https, sem cookies, sem cabeçalhos do motor (Accept-Language, User-Agent do Chromium…), e só quando o
// utilizador ligou a sincronização pelo Google. Testes: GoogleSyncTests (lista de endereços e varrimento do código).
//
// DNS e proxy: por não passar pelo motor, este cliente NÃO usa o «DNS cifrado» das Definições. Os dois nomes acima
// são resolvidos pelo DNS do Windows (o fornecedor de internet vê que o computador fala com a API da Google; o
// conteúdo vai em HTTPS) e é usado o proxy do Windows, se houver. Resolver pelo mesmo DoH seria mais um pedido fora
// do motor (ao fornecedor do DoH). Documentado em docs/sincronizacao.md («A exceção de rede»).
public static class GoogleEndpoints
{
    public const string Authorize = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string Token = "https://oauth2.googleapis.com/token";
    public const string Revoke = "https://oauth2.googleapis.com/revoke";
    public const string Files = "https://www.googleapis.com/drive/v3/files";
    public const string Upload = "https://www.googleapis.com/upload/drive/v3/files";

    // Com que conta se entrou: só o nome e o e-mail (o «fields» é obrigatório e limita a resposta a isto). Aceita o
    // âmbito drive.appdata (documentação da Google, about.get, «Authorization scopes»): não pede âmbito novo.
    public const string AboutUser = "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)";

    // Âmbito (não é um endereço pedido): só a pasta oculta da aplicação no Drive, classificado pela Google como não
    // sensível. O Éteris não vê mais nada do Drive do utilizador.
    public const string Scope = "https://www.googleapis.com/auth/drive.appdata";

    public static readonly IReadOnlyList<string> HttpHosts = ["oauth2.googleapis.com", "www.googleapis.com"];

    // O about.get só passa com esta query exata: um pedido futuro com outros «fields» (foto, quota, permissões…) é
    // recusado pelo GoogleOnlyHandler em vez de trazer mais dados da conta.
    private static readonly string AboutUserQuery = new Uri(AboutUser).Query;

    // O que o cliente HTTP da sincronização pode pedir.
    public static bool IsAllowedRequest(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath;
        return host switch
        {
            "oauth2.googleapis.com" => path is "/token" or "/revoke",
            "www.googleapis.com" => (path == "/drive/v3/about" && uri.Query == AboutUserQuery) || path == "/drive/v3/files" || path.StartsWith("/drive/v3/files/", StringComparison.Ordinal)
                                    || path == "/upload/drive/v3/files" || path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal),
            _ => false,
        };
    }

    // O que pode ser aberto no navegador do sistema (o ecrã de consentimento da Google).
    public static bool IsAllowedBrowserUrl(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.IdnHost.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath == "/o/oauth2/v2/auth";

    // O único cliente HTTP da sincronização. «inner» serve aos testes (Google Drive falso em memória).
    public static HttpClient CreateClient(HttpMessageHandler? inner = null)
    {
        inner ??= new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        // MaxResponseContentBufferSize: rede de segurança das respostas lidas por inteiro (listagens, respostas dos envios);
        // os ficheiros do Drive leem-se por blocos, com o limite de cada tipo (GoogleDriveTransport.ReadAsync).
        var client = new HttpClient(new GoogleOnlyHandler(inner)) { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = Format.SealedFile.MaxFileBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris-Sync/1");
        return client;
    }
}

// Recusa qualquer pedido fora da lista, antes de sair do processo.
public sealed class GoogleOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || !GoogleEndpoints.IsAllowedRequest(uri))
            throw new InvalidOperationException($"Pedido recusado pela sincronização: só a Google (OAuth e Drive) é permitida, não {request.RequestUri?.Host}.");
        return base.SendAsync(request, cancellationToken);
    }
}
