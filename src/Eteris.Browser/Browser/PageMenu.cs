namespace Eteris.Browser.Browser;

public enum PageCommand
{
    Back, Forward, Reload, Print, ViewSource, Inspect,
    OpenLinkNewTab, PreviewLink, OpenLinkPrivate, CopyLink,
    OpenImageNewTab, SaveImage, CopyImageUrl,
    Undo, Cut, Copy, Paste, SelectAll, SearchSelection, HideElement, SavePdf, ReadLater, CopyHighlightLink,
}

public sealed record PageContext(string? LinkUrl, string? ImageUrl, string Selection, bool IsEditable,
    bool CanGoBack, bool CanGoForward, bool IsPrivate, bool IsWebPage = true);

public sealed record PageMenuItem(PageCommand Command, string Glyph, string Label, string? Gesture = null, bool Enabled = true);

// Itens do menu de contexto da página, escolhidos pelo que está debaixo do rato. null = separador.
public static class PageMenu
{
    public const int QuoteLength = 22;

    public static IReadOnlyList<PageMenuItem?> Build(PageContext ctx, string engineName)
    {
        var items = new List<PageMenuItem?>();
        var hasSelection = ctx.Selection.Trim().Length > 0;

        if (ctx.LinkUrl is { Length: > 0 })
        {
            items.Add(new(PageCommand.OpenLinkNewTab, "", "Abrir link numa nova aba"));
            if (IsWebLink(ctx.LinkUrl)) items.Add(new(PageCommand.PreviewLink, "", "Espreitar o link", "Alt+clique"));
            if (!ctx.IsPrivate) items.Add(new(PageCommand.OpenLinkPrivate, "", "Abrir link numa janela privada"));
            items.Add(new(PageCommand.CopyLink, "", "Copiar endereço do link"));
            items.Add(null);
        }
        if (ctx.ImageUrl is { Length: > 0 })
        {
            items.Add(new(PageCommand.OpenImageNewTab, "", "Abrir imagem numa nova aba"));
            items.Add(new(PageCommand.SaveImage, "", "Guardar imagem"));
            items.Add(new(PageCommand.CopyImageUrl, "", "Copiar endereço da imagem"));
            items.Add(null);
        }
        if (ctx.IsEditable)
        {
            items.Add(new(PageCommand.Undo, "", "Anular", "Ctrl+Z"));
            items.Add(null);
            items.Add(new(PageCommand.Cut, "", "Cortar", "Ctrl+X", hasSelection));
            items.Add(new(PageCommand.Copy, "", "Copiar", "Ctrl+C", hasSelection));
            items.Add(new(PageCommand.Paste, "", "Colar", "Ctrl+V"));
            items.Add(new(PageCommand.SelectAll, "", "Selecionar tudo", "Ctrl+A"));
            items.Add(null);
        }
        else if (hasSelection)
        {
            items.Add(new(PageCommand.Copy, "", "Copiar", "Ctrl+C"));
        }
        if (hasSelection)
        {
            items.Add(new(PageCommand.SearchSelection, "", $"Pesquisar «{Quote(ctx.Selection)}» com {engineName}"));
            if (!ctx.IsEditable && ctx.IsWebPage)
                items.Add(new(PageCommand.CopyHighlightLink, "", "Copiar link para este destaque"));
            items.Add(null);
        }

        var plain = items.Count == 0;
        if (plain)
        {
            items.Add(new(PageCommand.Back, "", "Voltar", "Alt+←", ctx.CanGoBack));
            items.Add(new(PageCommand.Forward, "", "Avançar", "Alt+→", ctx.CanGoForward));
            items.Add(new(PageCommand.Reload, "", "Recarregar", "Ctrl+R"));
            items.Add(null);
            items.Add(new(PageCommand.Print, "", "Imprimir…", "Ctrl+P"));
            if (ctx.IsWebPage) items.Add(new(PageCommand.ReadLater, "\uE7BC", "Ler mais tarde"));
            if (ctx.IsWebPage) items.Add(new(PageCommand.SavePdf, "", "Guardar como PDF"));
            items.Add(new(PageCommand.ViewSource, "", "Ver código-fonte", "Ctrl+U"));
            if (ctx.IsWebPage) items.Add(new(PageCommand.HideElement, "", "Esconder um elemento…"));
        }
        items.Add(new(PageCommand.Inspect, "", "Inspecionar", "F12"));
        return Tidy(items);
    }

    // Só se espreita o que vem da web: mailto:, javascript: e afins não têm página para mostrar.
    public static bool IsWebLink(string? url)
        => url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private static string Quote(string selection)
    {
        var text = string.Join(' ', selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= QuoteLength ? text : text[..QuoteLength].TrimEnd() + "…";
    }

    private static List<PageMenuItem?> Tidy(List<PageMenuItem?> items)
    {
        var tidy = new List<PageMenuItem?>();
        foreach (var item in items)
        {
            if (item is null && (tidy.Count == 0 || tidy[^1] is null)) continue;
            tidy.Add(item);
        }
        while (tidy.Count > 0 && tidy[^1] is null) tidy.RemoveAt(tidy.Count - 1);
        return tidy;
    }
}
