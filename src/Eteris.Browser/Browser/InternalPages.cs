using System.Security.Cryptography;
using System.Text;

namespace Eteris.Browser.Browser;

// Ações das páginas eteris:// (apagar histórico, mudar definições) levam um token aleatório desta sessão.
// O esquema é "display isolated": nenhum site o consegue abrir, e o token impede pedidos forjados de outras origens.
public static class InternalPages
{
    public static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsAuthorized(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Query(uri, "t") is { } token
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Token));

    public static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == name) return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }
}
