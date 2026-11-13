namespace Eteris.Browser.Browser;

public enum PageCommand
{
    Back, Forward, Reload, Print, ViewSource, Inspect,
    OpenLinkNewTab, PreviewLink, OpenLinkSideBySide, OpenLinkPrivate, CopyLink,
    OpenImageNewTab, SaveImage, CopyImageUrl,
    Undo, Cut, Copy, Paste, SelectAll, SearchSelection, HideElement, SavePdf, ReadLater, CopyHighlightLink, PlayInVideoEngine,
    GeneratePassword, FillWith,
}

public sealed record PageContext(string? LinkUrl, string? ImageUrl, string Selection, bool IsEditable,
    bool CanGoBack, bool CanGoForward, bool IsPrivate, bool IsWebPage = true)
{
    // O campo onde se clicou, como o script do cofre o viu (null: fora de um campo de uma página http(s)).
    public VaultFieldInfo? Vault { get; init; }

    // O endereço do documento onde o menu abriu (null conta como página web): decide, com PageLinks, o que se pode abrir.
    public string? PageUrl { get; init; }
}

// IsPassword: um campo de palavra-passe; CanGenerate: «Oferecer para guardar palavras-passe» ligado e fora da janela
// privada (D-3).
public sealed record VaultFieldInfo(bool IsPassword, bool CanGenerate);

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
            var canOpen = PageLinks.CanOpenLink(ctx.LinkUrl, ctx.PageUrl);
            if (canOpen) items.Add(new(PageCommand.OpenLinkNewTab, "", "Abrir link numa nova aba"));
            if (IsWebLink(ctx.LinkUrl)) items.Add(new(PageCommand.PreviewLink, "", "Espreitar o link", "Alt+clique"));
            if (IsWebLink(ctx.LinkUrl)) items.Add(new(PageCommand.OpenLinkSideBySide, "", "Abrir link ao lado (ecrã dividido)"));
            if (canOpen && !ctx.IsPrivate) items.Add(new(PageCommand.OpenLinkPrivate, "", "Abrir link numa janela privada"));
            items.Add(new(PageCommand.CopyLink, "", "Copiar endereço do link"));
            items.Add(null);
        }
        if (ctx.ImageUrl is { Length: > 0 })
        {
            if (PageLinks.CanOpenImage(ctx.ImageUrl)) items.Add(new(PageCommand.OpenImageNewTab, "", "Abrir imagem numa nova aba"));
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
            // O cofre (C5, C6): «Preencher com… ›» (palavra-passe, contacto, cartão, documento, viagem) em qualquer campo de
            // uma página onde o script do cofre está; «Gerar palavra-passe forte» num campo de palavra-passe.
            if (ctx.Vault is { } vault)
            {
                items.Add(new(PageCommand.FillWith, "\uE8D7", "Preencher com…"));
                if (vault is { IsPassword: true, CanGenerate: true }) items.Add(new(PageCommand.GeneratePassword, "\uE8D7", "Gerar palavra-passe forte"));
                items.Add(null);
            }
        }
        else if (hasSelection)
        {
            items.Add(new(PageCommand.Copy, "", "Copiar", "Ctrl+C"));
        }
        if (hasSelection)
        {
            // Só numa página web: numa página interna a seleção pode ser o kit ou uma palavra-passe, e iria no endereço
            // para o motor de pesquisa e para o histórico.
            if (ctx.IsWebPage)
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
            if (ctx.IsWebPage && !ctx.IsPrivate) items.Add(new(PageCommand.ReadLater, "\uE7BC", "Ler mais tarde"));
            if (ctx.IsWebPage) items.Add(new(PageCommand.SavePdf, "", "Guardar como PDF"));
            items.Add(new(PageCommand.ViewSource, "", "Ver código-fonte", "Ctrl+U"));
            if (ctx.IsWebPage) items.Add(new(PageCommand.HideElement, "", "Esconder um elemento…"));
            // O vídeo não toca (H.264/AAC, DRM) e a deteção não o apanhou: forçar o motor de vídeo do Windows.
            if (ctx.IsWebPage) items.Add(new(PageCommand.PlayInVideoEngine, "", "Tocar no motor de vídeo"));
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
