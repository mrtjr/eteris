namespace Eteris.Browser.Browser;

// A regra única para o que uma página pode mandar abrir: o menu de contexto, os pop-ups e o texto largado passam por
// aqui. Só endereços da web abrem; as duas exceções são usos legítimos que continuam como estavam: links eteris://
// entre páginas internas e links file:// dentro de um documento file://. Decide-se pelo endereço do DOCUMENTO onde a
// ação nasceu (não por PageContext.IsWebPage): esse é falso também num about:blank aberto e escrito por um site.
public static class PageLinks
{
    private const string Internal = "eteris://";
    private const string Local = "file://";

    public static bool CanOpenLink(string? url, string? pageUrl)
        => PageMenu.IsWebLink(url)
           || (Starts(url, Internal) && Starts(pageUrl, Internal))
           || (Starts(url, Local) && Starts(pageUrl, Local));

    // Imagens: da web, blob: de uma página web, ou data:image/ raster (o SVG corre scripts e fica de fora).
    public static bool CanOpenImage(string? url)
        => PageMenu.IsWebLink(url)
           || Starts(url, "blob:http://") || Starts(url, "blob:https://")
           || (Starts(url, "data:image/") && !Starts(url, "data:image/svg"));

    // Pop-ups: também o branco (window.open() sem endereço, escrito depois) e o blob: da própria origem (o PDF ou a
    // página que a própria página gerou).
    public static bool CanOpenPopup(string? targetUrl, string? openerUrl)
    {
        if (string.IsNullOrEmpty(targetUrl) || targetUrl == "about:blank") return true;
        if (CanOpenLink(targetUrl, openerUrl)) return true;
        return PageMenu.IsWebLink(openerUrl)
               && Uri.TryCreate(openerUrl, UriKind.Absolute, out var opener)
               && Starts(targetUrl, "blob:" + opener.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool Starts(string? text, string prefix) => text is not null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
