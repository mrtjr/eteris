using System.Collections.Concurrent;
using System.Net;

namespace Eteris.Browser.Shields;

// HTTPS primeiro: navegações http:// para sites públicos vão por https://. Se o site não o suportar, o Éteris não
// recua para http:// em silêncio (como o modo «só HTTPS» do Firefox e o «HTTPS primeiro» do Brave): mostra o aviso
// «Este site não oferece ligação segura» e só segue por http:// depois de o utilizador escolher «Continuar» (até
// fechar o Éteris) ou «Sempre para este site» (exceção guardada, ver isException).
// A janela privada tem as suas próprias autorizações: o que se aceitou lá não se nota nas janelas normais.
public sealed class HttpsFirst(Func<string, bool>? isException = null, Func<DateTime>? clock = null)
{
    // Quanto tempo uma subida conta como «esta navegação foi subida pelo Éteris» (o https:// que não responde e cai
    // pelo tempo esgotado do sistema leva ~21 s; um https:// aberto mais tarde à mão é só um https://).
    public static readonly TimeSpan UpgradeWindow = TimeSpan.FromMinutes(2);

    // Quanto espera o https:// subido por uma resposta antes de ir ver se o http:// responde (o recuo rápido do Chrome
    // e do Firefox, ~3 s). Sem isto, um site só http:// com a porta 443 a descartar pacotes deixava a aba ~21 s em
    // branco (o tempo esgotado do Windows) antes do aviso.
    public static readonly TimeSpan FallbackAfter = TimeSpan.FromSeconds(3);

    // Depois de a falha ser tratada a marca sai, mas outra aba do mesmo endereço a falhar quase ao mesmo tempo (uma
    // reposta da sessão e outra nova) ainda tem direito ao aviso.
    public static readonly TimeSpan SiblingWindow = TimeSpan.FromSeconds(15);

    // Endereços https:// que foram subidos pelo Éteris, por contexto (normal/privado) e por endereço (não por host),
    // com o instante da subida. Saem quando o https:// responde (Settled) ou quando a falha é tratada (FailedUpgrade).
    private readonly ConcurrentDictionary<string, DateTime> _upgraded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _fellBack = new(StringComparer.Ordinal);
    private const int MaxUpgraded = 2000;
    private readonly ConcurrentDictionary<string, byte> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _allowedPrivate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    private static string Key(string secureUrl, bool isPrivate) => (isPrivate ? "p " : "n ") + secureUrl;

    // O https:// que o Éteris usaria para este http:// (null se fica como está), sem marcar nada — para o motor de
    // vídeo, que não passa pelo tratador de pedidos do Chromium.
    public string? Secure(string url, bool isPrivate = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!uri.IsDefaultPort || IsLocal(uri.Host) || IsAllowed(uri.Host, isPrivate)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
    }

    public string? Upgrade(string url, bool isPrivate = false)
    {
        if (Secure(url, isPrivate) is not { } secure) return null;
        if (_upgraded.Count >= MaxUpgraded) _upgraded.Clear();
        if (_fellBack.Count >= MaxUpgraded) _fellBack.Clear();
        _upgraded[Key(new Uri(secure).AbsoluteUri, isPrivate)] = _clock();
        return secure;
    }

    // O https:// subido respondeu (qualquer resposta que não seja um redirecionamento): o site fala HTTPS e a marca sai
    // — uma falha posterior desse endereço é uma falha de https:// como outra qualquer.
    public void Settled(string url, bool isPrivate = false)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            _upgraded.TryRemove(Key(uri.AbsoluteUri, isPrivate), out _);
    }

    // O https:// subido ainda não respondeu (nem falhou, nem foi tratado).
    public bool IsPending(string secureUrl, bool isPrivate = false)
        => Uri.TryCreate(secureUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
           && _upgraded.TryGetValue(Key(uri.AbsoluteUri, isPrivate), out var at) && _clock() - at <= UpgradeWindow;

    // O endereço que a sonda leva: a sonda é a única saída por http:// antes de o utilizador aceitar o aviso, e por isso
    // leva só o anfitrião e a porta — nunca o caminho, os parâmetros, o fragmento nem credenciais (que um endereço com
    // `http://utilizador:senha@…` traria). Não se usa GetLeftPart(Authority), que manteria o utilizador e a senha. Um
    // endereço que não seja absoluto devolve-se como veio.
    public static string ProbeUrl(string original)
        => Uri.TryCreate(original, UriKind.Absolute, out var uri)
            ? new UriBuilder(Uri.UriSchemeHttp, uri.IdnHost, uri.Port).Uri.AbsoluteUri
            : original;

    // A resposta da sondagem ao http:// (pedido HEAD sem cookies, pelo próprio motor) quando o https:// subido não
    // respondeu a tempo: o site responde por http:// — o aviso aparece já, como depois de uma falha. Um
    // redirecionamento para o https:// do mesmo site quer dizer que o site fala HTTPS e só está lento: continua-se à
    // espera. Sem resposta (0) também: se o https:// falhar, o aviso chega pela falha.
    public static bool ProbeSaysInsecure(int status, string? location, string httpUrl)
    {
        if (status <= 0) return false;
        if (status is >= 300 and < 400 && !string.IsNullOrWhiteSpace(location) && Uri.TryCreate(httpUrl, UriKind.Absolute, out var from)
            && Uri.TryCreate(from, location.Trim(), out var to) && to.Scheme == Uri.UriSchemeHttps
            && Data.SiteScope.OfHost(to.Host) == Data.SiteScope.OfHost(from.Host))
            return false;
        return true;
    }

    // O endereço http:// original quando foi o Éteris a subir este pedido para https:// há pouco (null se o site foi
    // aberto em https:// por quem navegou: aí uma falha é só uma falha). Consome a marca: a mesma subida só recua
    // uma vez (um http:// aceite que o servidor manda de volta para o mesmo https:// não volta ao aviso).
    public string? FailedUpgrade(string failedUrl) => FailedUpgrade(failedUrl, isPrivate: false);

    public string? FailedUpgrade(string failedUrl, bool isPrivate)
    {
        if (!Uri.TryCreate(failedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var key = Key(uri.AbsoluteUri, isPrivate);
        var now = _clock();
        if (_upgraded.TryRemove(key, out var at) && now - at <= UpgradeWindow) _fellBack[key] = now;
        else if (!(_fellBack.TryGetValue(key, out var fell) && now - fell <= SiblingWindow)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri.ToString();
    }

    // «Continuar sem segurança»: este host segue por http:// até fechar o Éteris (só na memória).
    public void Allow(string host, bool isPrivate) => (isPrivate ? _allowedPrivate : _allowed)[host] = 0;

    public bool IsAllowed(string host, bool isPrivate)
        => (isPrivate ? _allowedPrivate : _allowed).ContainsKey(host) || isException?.Invoke(host) == true;

    public bool IsAllowedUrl(string url, bool isPrivate)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri.Host, isPrivate);

    // A sessão privada acabou (última janela privada fechada, ou «Terminar sessão privada»): as escolhas de http://
    // feitas lá — e os endereços que lá foram subidos — não sobrevivem a ela.
    public void ForgetPrivate()
    {
        _allowedPrivate.Clear();
        foreach (var key in _upgraded.Keys.Where(k => k.StartsWith("p ", StringComparison.Ordinal)).ToList()) _upgraded.TryRemove(key, out _);
        foreach (var key in _fellBack.Keys.Where(k => k.StartsWith("p ", StringComparison.Ordinal)).ToList()) _fellBack.TryRemove(key, out _);
    }

    // «Voltar a tentar HTTPS» no cadeado e «Repor» do site: a próxima visita volta a tentar https:// primeiro.
    public void Forget(string host)
    {
        _allowed.TryRemove(host, out _);
        _allowedPrivate.TryRemove(host, out _);
    }

    // As falhas do https:// subido que querem dizer «este site não fala HTTPS» (o http:// ainda pode funcionar):
    // ligação (-1xx), certificado (-2xx), tempo esgotado (-7, também o do recuo rápido do Éteris) e respostas que só o servidor https:// estraga
    // (redireciona de volta para http:// sem fim, responde vazio ou malformado). Não contam as que também impediriam o
    // http://: o nome não existe ou não resolve, o computador está sem rede, o endereço é inalcançável, o proxy
    // falhou — essas dão a página de erro com o endereço que a pessoa abriu.
    public static bool IsUpgradeFailure(int errorCode)
        => errorCode is -7 or -310 or -320 or -321 or -324 or -325 or -331 or -337
           or (<= -100 and > -300 and not (-105 or -137 or -106 or -109 or -115 or -127 or -130 or -131));

    private static bool IsLocal(string host)
        => !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
}
