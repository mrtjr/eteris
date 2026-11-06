using System.Collections.Concurrent;
using System.Net;

namespace Eteris.Browser.Shields;

// HTTPS primeiro: navegações http:// para sites públicos vão por https://; se o site não o suportar,
// volta-se a http:// para esse host até ao fim da sessão.
public sealed class HttpsFirst
{
    private readonly ConcurrentDictionary<string, byte> _upgraded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _httpOnly = new(StringComparer.OrdinalIgnoreCase);

    public string? Upgrade(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!uri.IsDefaultPort || IsLocal(uri.Host) || _httpOnly.ContainsKey(uri.Host)) return null;

        _upgraded[uri.Host] = 0;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
    }

    public string? FallbackFor(string failedUrl)
    {
        if (!Uri.TryCreate(failedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!_upgraded.TryRemove(uri.Host, out _)) return null;
        _httpOnly[uri.Host] = 0;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri.ToString();
    }

    // Erros de ligação (-1xx) e de certificado (-2xx) do Chromium: o site não fala HTTPS.
    public static bool IsUpgradeFailure(int errorCode) => errorCode is <= -100 and > -300;

    private static bool IsLocal(string host)
        => !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
}
