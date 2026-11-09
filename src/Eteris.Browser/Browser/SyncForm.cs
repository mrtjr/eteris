using System.Text;
using CefSharp;

namespace Eteris.Browser.Browser;

// Formulário (application/x-www-form-urlencoded) enviado por POST a uma página eteris:// — o perfil importado e o
// seu código vão no corpo do pedido, nunca no endereço (que pode ficar em registos e no histórico).
internal static class SyncForm
{
    public const int MaxBytes = 32 * 1024 * 1024;

    public static Dictionary<string, string> Parse(IRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) || request.PostData is not { } post) return [];
        var body = new List<byte>();
        foreach (var element in post.Elements)
        {
            if (element.Type != PostDataElementType.Bytes || element.Bytes is not { } bytes) continue;
            if (body.Count + bytes.Length > MaxBytes) return [];
            body.AddRange(bytes);
        }
        return ParseBody(Encoding.ASCII.GetString(body.ToArray()));
    }

    public static Dictionary<string, string> ParseBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result.TryAdd(Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')), Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
        }
        return result;
    }
}
