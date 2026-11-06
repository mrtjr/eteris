namespace Eteris.Browser.Shields;

// «Escurecer este site»: modo escuro por site, à maneira do modo filtro do Dark Reader — a página é invertida e
// rodada no matiz (o branco passa a quase preto, as cores mantêm o tom) e imagens/vídeos são invertidos de volta.
// Entra antes da primeira pintura (script registado para cada documento novo), por isso não há clarão branco.
public static class DarkPages
{
    public const string StyleId = "eteris-dark";

    public const string Css = """
        html { filter: invert(.9) hue-rotate(180deg) !important; background: #fff !important; }
        img, video, picture, canvas, iframe, embed, object, svg image, [style*="background-image"] {
          filter: invert(1) hue-rotate(180deg) !important;
        }
        """;

    public static bool IsCandidate(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host.Length > 0;

    // Corre no início de cada documento (antes de existir <head>): pendura o estilo na raiz.
    public static string EarlyScript => Live(true);

    public static string Live(bool on)
    {
        var css = System.Text.Json.JsonSerializer.Serialize(Css);
        return on
            ? $$"""
              (() => {
                if (document.getElementById('{{StyleId}}')) return;
                const style = document.createElement('style');
                style.id = '{{StyleId}}';
                style.textContent = {{css}};
                // No início do documento ainda não há <html>: espera-se pela raiz (continua antes da primeira pintura).
                const add = () => { const root = document.head || document.documentElement; if (!root) return false; root.appendChild(style); return true; };
                if (!add()) new MutationObserver((_, observer) => { if (add()) observer.disconnect(); }).observe(document, { childList: true });
              })();
              """
            : $$"""
              (() => { const style = document.getElementById('{{StyleId}}'); if (style) style.remove(); })();
              """;
    }
}
