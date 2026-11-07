namespace Eteris.Browser.Shields;

// Sinais de privacidade que o Éteris envia a todos os sites: «Não rastrear» e Global Privacy Control, no cabeçalho
// de cada pedido e (o GPC) também no JavaScript, que é onde os avisos de cookies e o Consent Mode o leem.
public static class PrivacySignals
{
    public static readonly IReadOnlyList<(string Name, string Value)> Headers = [("DNT", "1"), ("Sec-GPC", "1")];

    // navigator.globalPrivacyControl === true. Registado pelo ScriptletInjector antes de qualquer script da página
    // e em todos os frames do processo; se o motor já expuser o valor verdadeiro, não mexe.
    public const string GpcScript = """
        (() => {
          try {
            const proto = Navigator.prototype;
            const own = Object.getOwnPropertyDescriptor(proto, 'globalPrivacyControl');
            if (own && own.get && own.get.call(navigator) === true) return;
            Object.defineProperty(proto, 'globalPrivacyControl', {
              get: function globalPrivacyControl() { return true; }, configurable: true, enumerable: true,
            });
          } catch (e) { }
        })();
        """;

    // Páginas onde o sinal vale: os sites e as páginas internas (eteris://privacy/ verifica-o ao vivo).
    public static bool AppliesTo(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "eteris";

    public static string? ScriptFor(string url) => AppliesTo(url) ? GpcScript : null;

    // O script registado numa navegação: sinais, scriptlets do site e extras da janela, por esta ordem.
    public static string? Compose(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return present.Count == 0 ? null : string.Join("\n;\n", present);
    }
}
