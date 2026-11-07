using System.Collections.Concurrent;
using System.Net;

namespace Eteris.Browser.Shields;

// HTTPS primeiro: navegações http:// para sites públicos vão por https://. Se o site não o suportar, o Éteris não
// recua para http:// em silêncio (como o modo «só HTTPS» do Firefox e o «HTTPS primeiro» do Brave): mostra o aviso
// «Este site não oferece ligação segura» e só segue por http:// depois de o utilizador escolher «Continuar» (até
// fechar o Éteris) ou «Sempre para este site» (exceção guardada, ver isException).
// A janela privada tem as suas próprias autorizações: o que se aceitou lá não se nota nas janelas normais.
public sealed class HttpsFirst(Func<string, bool>? isException = null)
{
    // Endereços https:// que foram subidos pelo Éteris (por endereço, não por host: duas abas do mesmo site a falhar
    // ao mesmo tempo — uma reposta da sessão e outra nova — têm as duas direito ao aviso).
    private readonly ConcurrentDictionary<string, byte> _upgraded = new(StringComparer.Ordinal);
    private const int MaxUpgraded = 2000;
    private readonly ConcurrentDictionary<string, byte> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _allowedPrivate = new(StringComparer.OrdinalIgnoreCase);

    public string? Upgrade(string url, bool isPrivate = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!uri.IsDefaultPort || IsLocal(uri.Host) || IsAllowed(uri.Host, isPrivate)) return null;

        var secure = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (_upgraded.Count >= MaxUpgraded) _upgraded.Clear();
        _upgraded[secure.AbsoluteUri] = 0;
        return secure.ToString();
    }

    // O endereço http:// original quando foi o Éteris a subir este pedido para https:// (null se o site foi
    // aberto em https:// por quem navegou: aí uma falha é só uma falha).
    public string? FailedUpgrade(string failedUrl)
    {
        if (!Uri.TryCreate(failedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!_upgraded.ContainsKey(uri.AbsoluteUri)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri.ToString();
    }

    // «Continuar sem segurança»: este host segue por http:// até fechar o Éteris (só na memória).
    public void Allow(string host, bool isPrivate) => (isPrivate ? _allowedPrivate : _allowed)[host] = 0;

    public bool IsAllowed(string host, bool isPrivate)
        => (isPrivate ? _allowedPrivate : _allowed).ContainsKey(host) || isException?.Invoke(host) == true;

    public bool IsAllowedUrl(string url, bool isPrivate)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri.Host, isPrivate);

    // «Esquecer a exceção» no cadeado: a próxima visita volta a tentar https:// primeiro.
    public void Forget(string host)
    {
        _allowed.TryRemove(host, out _);
        _allowedPrivate.TryRemove(host, out _);
    }

    // Erros de ligação (-1xx) e de certificado (-2xx) do Chromium: o site não fala HTTPS. Não contam os que
    // também impediriam o http:// (o nome não existe, o computador está sem rede): esses dão a página de erro.
    // -7 (tempo esgotado) é o que dá um servidor que deixa a porta 443 sem resposta.
    public static bool IsUpgradeFailure(int errorCode)
        => errorCode is -7 or (<= -100 and > -300 and not (-105 or -137 or -106));

    private static bool IsLocal(string host)
        => !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
}
