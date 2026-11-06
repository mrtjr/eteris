namespace Eteris.Browser.Omnibox;

// O que abrir quando se larga algo na faixa de abas: um link/texto (como se escrito na omnibox) ou um ficheiro.
public static class DropTarget
{
    public static string? FromText(string text, SearchEngine engine)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line is { Length: > 0 } ? OmniboxClassifier.Classify(line, engine)?.Url : null;
    }

    public static string FromFile(string path) => new Uri(path).AbsoluteUri;
}
