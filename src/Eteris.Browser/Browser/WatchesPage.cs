using System.Globalization;
using System.IO;
using System.Text.Json;
using Eteris.Browser.Tabs;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Browser;

// eteris://vigias/ — as vigias de preço e de pedaços, com o estado de cada uma, o último preço (e a curva das leituras)
// e as ações (abrir, pausar, retomar, apagar), que levam o token desta sessão. Daqui saem também os textos dos avisos
// (Notice), que o Éteris mostra só dentro dele (decisão 3). A janela privada não vigia nem mostra as vigias do perfil.
public static class WatchesPage
{
    public const string Url = "eteris://vigias/";

    // A curva de cada vigia: as últimas leituras de preço (as mais antigas ficam de fora da página).
    private const int MaxPoints = 60;

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(WatchesPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.watches.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // O estado de uma vigia, como a página o mostra. A hora de «falhou desde» é a hora local do PC.
    public static string Status(PageWatch w, DateTime nowUtc)
    {
        if (w.Paused) return "em pausa";
        switch (w.Status)
        {
            case WatchStatus.Blocked:
                return "este site não deixa vigiar sozinho — o preço é lido quando abre a página";
            case WatchStatus.Missing:
                return "o pedaço desapareceu";
            case WatchStatus.Failing:
                var since = w.FailingSinceUtc ?? w.LastCheckUtc ?? w.CreatedUtc;
                return "falhou desde " + since.ToLocalTime().ToString("dd/MM HH:mm", Pt);
        }
        if (w.Status == WatchStatus.New || w.LastCheckUtc is not { } last) return "à espera da 1.ª verificação";
        var ago = nowUtc - last;
        return ago < TimeSpan.FromHours(1)
            ? $"verificado há {Math.Max(1, (int)ago.TotalMinutes)} min"
            : $"verificado há {(int)ago.TotalHours} h";
    }

    // O aviso de uma verificação; null quando não há nada a dizer. O preço antigo é o da última leitura boa.
    public static string? Notice(PageWatch before, WatchOutcome outcome, WatchCheck check)
    {
        var currency = check.Currency ?? before.Currency;
        string Price(decimal? value) => value is { } v ? PriceText.Format(v, currency) : "?";
        return outcome switch
        {
            WatchOutcome.PriceDropped => $"O preço baixou: {before.Title} — {Price(before.LastPrice)} → {Price(check.Price)}",
            WatchOutcome.ReachedTarget => $"Chegou ao preço que queria: {before.Title} — {Price(check.Price)}",
            WatchOutcome.Changed => $"Mudou: {before.Title}",
            WatchOutcome.BecameBlocked => $"{before.Site} não deixa vigiar sozinho: o preço passa a ser lido quando abrir a página",
            WatchOutcome.PieceMissing => $"A vigia de «{before.Title}» perdeu o pedaço que vigiava",
            _ => null,
        };
    }

    // A página. O <body data-last-prices="…"> lista os últimos preços (formato invariante, pelo menos duas casas,
    // separados por espaços): a página atualiza-o a cada leitura da lista, e o teste ao vivo da vigia lê-o.
    public static string Render(IReadOnlyList<PageWatch> watches, Func<long, IReadOnlyList<(DateTime AtUtc, decimal Price)>> prices,
        DateTime nowUtc, bool isPrivate = false)
    {
        if (isPrivate) watches = [];
        return Template.Value
            .Replace("{{LAST_PRICES}}", LastPrices(watches))
            .Replace("{{DATA}}", Data(watches, prices, nowUtc, isPrivate));
    }

    // Os dados da página em JSON: servidos dentro dela e, depois, pela rota /list (e pelas ações, que devolvem a lista
    // nova). O serializador escapa «<», «>» e «&»: um título de página nunca fecha o <script>.
    public static string Data(IReadOnlyList<PageWatch> watches, Func<long, IReadOnlyList<(DateTime AtUtc, decimal Price)>> prices,
        DateTime nowUtc, bool isPrivate = false)
    {
        var items = watches.Select(w =>
        {
            var history = w.Kind == WatchKind.Price ? prices(w.Id) : [];
            var points = history.TakeLast(MaxPoints).ToList();
            var low = history.Count >= 2 ? history.MinBy(p => p.Price) : default((DateTime AtUtc, decimal Price)?);
            return new
            {
                id = w.Id,
                url = w.Url,
                site = w.Site,
                title = w.Title.Length > 0 ? w.Title : w.Url,
                kind = w.Kind == WatchKind.Price ? "price" : "change",
                price = w.LastPrice is { } p ? PriceText.Format(p, w.Currency) : null,
                text = w.Kind == WatchKind.Change ? Excerpt(w.LastText) : null,
                status = Status(w, nowUtc),
                state = w.Paused ? "paused" : w.Status.ToString().ToLowerInvariant(),
                paused = w.Paused,
                every = Every(w.IntervalMinutes),
                session = w.UseSession,
                target = w.TargetPrice is { } t ? PriceText.Format(t, w.Currency) : null,
                low = low is { } l ? PriceText.Format(l.Price, w.Currency) + " em " + l.AtUtc.ToLocalTime().ToString("dd/MM", Pt) : null,
                points = points.Select(x => (double)x.Price).ToArray(),
                letter = SiteBadge.Letter(w.Url) ?? "?",
                color = SiteBadge.Palette[SiteBadge.ColorIndex(w.Url)],
            };
        });
        return JsonSerializer.Serialize(new
        {
            token = isPrivate ? "" : InternalPages.Token,
            isPrivate,
            lastPrices = LastPrices(watches),
            watches = items,
        });
    }

    private static string LastPrices(IReadOnlyList<PageWatch> watches)
        => string.Join(" ", watches.Where(w => w.LastPrice is not null)
            .Select(w => w.LastPrice!.Value.ToString("0.00##########", CultureInfo.InvariantCulture)));

    private static string Every(int minutes)
    {
        minutes = Math.Max(minutes, WatchSchedule.MinIntervalMinutes);
        return minutes % 60 == 0 ? $"a cada {minutes / 60} h" : $"a cada {minutes} min";
    }

    private static string? Excerpt(string? text)
        => text is null ? null : text.Length <= 160 ? text : text[..157].TrimEnd() + "…";
}
