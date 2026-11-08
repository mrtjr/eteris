using System.Collections.Concurrent;
using Eteris.Browser.Data;

namespace Eteris.Browser.Shields;

// Categorias do relatório de privacidade (eteris://privacy/). Só agregados: por dia, a categoria e o domínio do
// rastreador (nunca o site onde estava, nunca o endereço) — o relatório não pode virar um segundo histórico.
public static class PrivacyCategory
{
    public const string Lists = "listas";
    public const string Strict = "rigoroso";
    public const string Cloaked = "cname";
    public const string User = "regra";
    public const string LocalNetwork = "rede-local";
    public const string Debounce = "salto";
    public const string Forgotten = "esquecido";

    // Os que contam como «pedidos de rastreio» (o total, o gráfico, as empresas e os rastreadores): as listas, os
    // escondidos por CNAME e os que o utilizador bloqueou. O modo rigoroso corta também CDNs e embeds (Stripe, YouTube)
    // e conta à parte; a rede local não é rastreio — fica só no seu mosaico.
    public static readonly string[] Tracking = [Lists, Cloaked, User];

    public static string? Of(ObservedVerdict verdict) => verdict switch
    {
        ObservedVerdict.Blocked => Lists,
        ObservedVerdict.Strict => Strict,
        ObservedVerdict.Cloaked => Cloaked,
        ObservedVerdict.UserBlocked => User,
        ObservedVerdict.LocalNetwork => LocalNetwork,
        _ => null,
    };
}

// Contadores ainda por gravar, juntados em memória e escritos em lote pelo mesmo temporizador do painel semanal
// (AppData.FlushShieldStats, a cada 20 s e à saída). As janelas privadas nunca chegam aqui.
public static class PrivacyLedger
{
    private static ConcurrentDictionary<(string Category, string Subject), int> _pending = new();

    // O auto-teste (eteris://privacy/teste) faz de propósito um pedido à rede local: não entra no relatório.
    public static bool Counts(string? pageUrl)
        => !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !Browser.SelfTest.IsTestHost(page.Host);

    public static void Count(string category, string? subject)
    {
        var key = (category, subject is { Length: > 0 } s ? SiteScope.OfHost(s) : string.Empty);
        _pending.AddOrUpdate(key, 1, (_, n) => n + 1);
    }

    // Devolve (e esvazia) o que ainda não foi gravado.
    public static IReadOnlyList<(string Category, string Subject, int Count)> Take()
    {
        var taken = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<(string, string), int>());
        return taken.Select(p => (p.Key.Item1, p.Key.Item2, p.Value)).ToList();
    }
}

public sealed record PrivacyEvent(DateOnly Day, string Category, string Subject, int Count);

public sealed record PrivacyReportData(
    int Days,
    int Blocked,
    IReadOnlyList<int> PerDay,
    IReadOnlyDictionary<string, int> ByCategory,
    IReadOnlyList<(string Name, int Count)> TopCompanies,
    IReadOnlyList<(string Domain, string? Company, int Count)> TopTrackers,
    IReadOnlyList<string> CloakedExamples,
    string Summary);

// O relatório legível: últimos 7 ou 30 dias a partir dos agregados guardados.
public static class PrivacyReport
{
    public const int KeepDays = 35;

    public static PrivacyReportData Build(IReadOnlyList<PrivacyEvent> events, DateOnly today, int days, CompanyMap? owners = null)
    {
        owners ??= CompanyMap.Default;
        var from = today.AddDays(-(days - 1));
        var window = events.Where(e => e.Day >= from && e.Day <= today).ToList();
        var perDay = new int[days];
        foreach (var e in window.Where(e => PrivacyCategory.Tracking.Contains(e.Category)))
            perDay[e.Day.DayNumber - from.DayNumber] += e.Count;
        var byCategory = window.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(e => e.Count));
        var trackers = window.Where(e => PrivacyCategory.Tracking.Contains(e.Category) && e.Subject.Length > 0)
            .GroupBy(e => e.Subject)
            .Select(g => (Domain: g.Key, Company: owners.OwnerOf(g.Key), Count: g.Sum(e => e.Count)))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Domain, StringComparer.Ordinal).ToList();
        var companies = trackers.Where(t => t.Company is not null).GroupBy(t => t.Company!)
            .Select(g => (Name: g.Key, Count: g.Sum(t => t.Count)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).Take(6).ToList();
        var cloaked = window.Where(e => e.Category == PrivacyCategory.Cloaked && e.Subject.Length > 0)
            .GroupBy(e => e.Subject).OrderByDescending(g => g.Sum(e => e.Count)).Select(g => g.Key).Take(3).ToList();
        var blocked = perDay.Sum();
        return new PrivacyReportData(days, blocked, perDay, byCategory, companies, trackers.Take(8).ToList(), cloaked,
            Summarise(days, blocked, companies, byCategory));
    }

    // A frase do topo, em linguagem de gente.
    public static string Summarise(int days, int blocked, IReadOnlyList<(string Name, int Count)> companies, IReadOnlyDictionary<string, int> byCategory)
    {
        var period = days == 7 ? "Nos últimos 7 dias" : $"Nos últimos {days} dias";
        if (blocked == 0 && byCategory.Count == 0) return $"{period} ainda não há nada para contar: o relatório enche-se à medida que navega.";
        var text = blocked == 1 ? $"{period}, o Escudo travou 1 pedido de rastreio" : $"{period}, o Escudo travou {Number(blocked)} pedidos de rastreio";
        if (companies.Count > 0 && blocked > 0)
        {
            var top = companies[0];
            var share = (int)Math.Round(100.0 * top.Count / blocked);
            text += share >= 1 ? $" — {share} % vinham da {top.Name}" : string.Empty;
        }
        var skips = byCategory.GetValueOrDefault(PrivacyCategory.Debounce);
        if (skips > 0) text += skips == 1 ? " e saltou 1 redirecionamento de rastreio" : $" e saltou {Number(skips)} redirecionamentos de rastreio";
        // O modo rigoroso corta também o que não é rastreio (CDNs, embeds): à parte, nunca no total.
        var strict = byCategory.GetValueOrDefault(PrivacyCategory.Strict);
        if (strict > 0) text += strict == 1 ? "; o modo rigoroso travou mais 1 pedido a outros domínios" : $"; o modo rigoroso travou mais {Number(strict)} pedidos a outros domínios";
        return text + ".";
    }

    public static string Number(int n) => n.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("pt-PT"));
}
