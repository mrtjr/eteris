namespace Eteris.Browser.Omnibox;

// O que abrir quando se larga algo na faixa de abas: um link/texto (como se escrito na omnibox) ou um ficheiro.
public static class DropTarget
{
    public static string? FromText(string text, SearchEngine engine)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is not { Length: > 0 }) return null;
        // Só vale o que abre como página da web (uma pesquisa também é https): file://, eteris:// e about: ficam de fora.
        return OmniboxClassifier.Classify(line, engine)?.Url is { } url && Browser.PageMenu.IsWebLink(url) ? url : null;
    }

    // O Chromium marca assim um arrasto que nasceu numa página: o que vem dele nunca é um ficheiro do utilizador.
    public const string RendererTaintFormat = "chromium/x-renderer-taint";

    public static string FromFile(string path) => new Uri(path).AbsoluteUri;
}
