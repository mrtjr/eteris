using System.Text.Json;

namespace Eteris.Browser.Watching;

// A resposta do WatchScripts.Read passa a PageReading. O preço é o dos dados estruturados (StructuredPrice); só sem ele
// se lê o número do pedaço apontado. Uma resposta estragada (ou de outro formato) é uma leitura vazia — sem título, sem
// preço, sem pedaço —, que o WatchRules conta como página que não carregou; nunca uma exceção.
//
// O desafio: «challenge» (a página de desafio: #challenge-form, #cf-challenge-running) conta sempre, mesmo que o pedaço
// apontado também lá esteja; «captcha» (um reCAPTCHA ou um iframe de CAPTCHA) só conta numa página onde nada se leu,
// porque também aparece nos formulários (newsletter, avaliações) de páginas de produto normais.
public static class WatchReadingParser
{
    public static PageReading Parse(string json, string finalUrl)
    {
        var empty = new PageReading(null, null, false, "", finalUrl, false);
        if (string.IsNullOrWhiteSpace(json)) return empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return empty;

            var piece = Text(root, "sel");
            var pieceFound = Flag(root, "selFound") && piece is not null;
            if (!pieceFound) piece = null;

            var hint = StructuredPrice.Code(Text(root, "currency")) ?? PriceText.CurrencyOf(piece);
            var price = StructuredPrice.From(Texts(root, "ld"), Texts(root, "itemprop"), Texts(root, "meta"), hint);
            if (price is null && pieceFound && PriceText.Parse(piece) is { } value && value > 0)
                price = new PriceReading(value, PriceText.CurrencyOf(piece) ?? hint, "texto");

            var challenge = Flag(root, "challenge") || (Flag(root, "captcha") && price is null && !pieceFound);
            return new PageReading(price, piece, pieceFound, Text(root, "title") ?? "", finalUrl, challenge);
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    // A resposta do WatchScripts.PickPiece ({"selector","text"}, que o PageScript devolve como texto JSON; null com Esc).
    // A página pode mexer nela: só passa um seletor que também serviria para esconder elementos (HiddenElements).
    public static (string Selector, string Text)? Pick(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "selector") is not { } selector) return null;
            if (!Shields.HiddenElements.IsSafeSelector(selector)) return null;
            return (selector, Text(root, "text") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> Texts(JsonElement root, string name)
    {
        var texts = new List<string>();
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) texts.Add(text);
        return texts;
    }
}
