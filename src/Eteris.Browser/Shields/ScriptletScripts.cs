using System.Text.Json;

namespace Eteris.Browser.Shields;

public static class ScriptletScripts
{
    // O script é registado antes de a página carregar e corre em todos os documentos novos do separador:
    // limita-se ao frame de topo do site para o qual foi calculado (um redirecionamento leva a outro site).
    public static string? Wrap(string script, string url)
    {
        if (script.Length == 0) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http", StringComparison.Ordinal)) return null;

        // O uBlock define scriptletGlobals ao injetar e os scriptlets (via safeSelf) contam com ela.
        return $$"""
            if (window.top === window && location.hostname === {{JsonSerializer.Serialize(uri.Host)}}) {
              try {
                const scriptletGlobals = {};
            {{script}}
              } catch (e) { }
            }
            """;
    }
}
