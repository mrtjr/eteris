using System.Text.Json;

namespace Eteris.Browser.Watching;

// O preço que a página declara nos dados estruturados, pela ordem: JSON-LD (um objeto com «offers»: Product e os seus
// subtipos, em qualquer sítio, @graph incluído), itemprop="price" e meta product:price:amount. É este que conta, nunca o
// primeiro número do texto (prestações, «de … por …»).
//
// - Vários preços nas ofertas do mesmo produto: conta o mais baixo, para que a mesma lista noutra ordem nunca pareça
//   uma descida.
// - Um preço 0 (ou negativo) não é preço: as páginas esgotadas escrevem «0.00» e isso daria uma descida falsa.
// - A moeda: a que a oferta declara; senão o símbolo no próprio valor («R$ 907,99»); senão currencyHint (a que a página
//   declara à parte, ou o símbolo do pedaço).
public static class StructuredPrice
{
    public static PriceReading? From(IReadOnlyList<string> jsonLd, IReadOnlyList<string> itemprop, IReadOnlyList<string> meta,
        string? currencyHint)
    {
        var hint = Code(currencyHint);
        foreach (var block in jsonLd)
            if (FromJsonLd(block, hint) is { } reading) return reading;
        return FromTexts(itemprop, hint, "itemprop") ?? FromTexts(meta, hint, "meta");
    }

    // Um código ISO 4217 (três letras), em maiúsculas; outra coisa não é moeda.
    public static string? Code(string? currency)
    {
        var code = currency?.Trim();
        return code is { Length: 3 } && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : null;
    }

    private static PriceReading? FromJsonLd(string? block, string? hint)
    {
        if (string.IsNullOrWhiteSpace(block)) return null;
        try
        {
            using var document = JsonDocument.Parse(block, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return FirstProduct(document.RootElement, hint);
        }
        catch (JsonException)
        {
            return null;   // um bloco partido não conta; os outros sim
        }
    }

    // O primeiro objeto (em profundidade, pela ordem do documento) cujas ofertas dão um preço.
    private static PriceReading? FirstProduct(JsonElement node, string? hint)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                if (FirstProduct(item, hint) is { } found) return found;
            return null;
        }
        if (node.ValueKind != JsonValueKind.Object) return null;

        if (node.TryGetProperty("offers", out var offers) && FromOffers(offers, hint) is { } reading) return reading;
        foreach (var property in node.EnumerateObject())
            if (property.Name != "offers" && FirstProduct(property.Value, hint) is { } found) return found;
        return null;
    }

    // O preço mais baixo das ofertas (Offer.price, AggregateOffer.lowPrice ou priceSpecification.price).
    private static PriceReading? FromOffers(JsonElement offers, string? hint)
    {
        PriceReading? best = null;
        foreach (var offer in Items(offers))
        {
            if (offer.ValueKind != JsonValueKind.Object) continue;
            var currency = Text(offer, "priceCurrency");
            var candidates = new List<(JsonElement Value, string? Currency)>();
            if (offer.TryGetProperty("price", out var price)) candidates.Add((price, currency));
            if (offer.TryGetProperty("lowPrice", out var low)) candidates.Add((low, currency));
            if (offer.TryGetProperty("priceSpecification", out var specs))
                foreach (var spec in Items(specs))
                    if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("price", out var specPrice))
                        candidates.Add((specPrice, Text(spec, "priceCurrency") ?? currency));

            foreach (var (value, declared) in candidates)
            {
                var (amount, raw) = Amount(value);
                if (amount is not { } v || v <= 0) continue;
                if (best is null || v < best.Value)
                    best = new PriceReading(v, Code(declared) ?? PriceText.CurrencyOf(raw) ?? hint, "ld");
            }
        }
        return best;
    }

    private static PriceReading? FromTexts(IReadOnlyList<string> texts, string? hint, string source)
    {
        foreach (var text in texts)
            if (PriceText.Parse(text) is { } value && value > 0)
                return new PriceReading(value, PriceText.CurrencyOf(text) ?? hint, source);
        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [element];

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Um número do JSON lê-se tal como está (o JSON-LD usa o ponto decimal); um texto passa pelo PriceText.
    private static (decimal? Amount, string? Raw) Amount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => (value.TryGetDecimal(out var number) ? number : null, null),
        JsonValueKind.String => (PriceText.Parse(value.GetString()), value.GetString()),
        _ => (null, null),
    };
}
