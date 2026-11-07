using System.IO;
using System.Reflection;
using System.Text.Json;
using Eteris.Browser.Data;

namespace Eteris.Browser.Browser;

// eteris://settings — cada alteração é enviada para eteris://settings/set?k=…&v=…&t=token e aplicada de imediato.
public static class SettingsPage
{
    public const string Url = "eteris://settings/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.settings.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Version
        => typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
           ?? "0.0.0";

    public sealed record About(string Chromium, string CefSharp, bool FullEngine, IReadOnlyList<Shields.ListSummary> Lists,
        string UpdateStatus, bool CanCheck);

    public static string ListsLine(IReadOnlyList<Shields.ListSummary> lists, DateTime nowUtc)
    {
        var rules = lists.Sum(l => l.Rules);
        var newest = lists.Where(l => !l.Bundled).Select(l => l.UpdatedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var when = newest == DateTime.MinValue ? "cópia incluída no Éteris"
            : (nowUtc - newest).TotalDays < 1 ? "atualizadas hoje"
            : (int)(nowUtc - newest).TotalDays == 1 ? "atualizadas ontem"
            : $"atualizadas há {(int)(nowUtc - newest).TotalDays} dias";
        return $"{lists.Count} listas · {rules.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-PT"))} regras · {when}";
    }

    public static string Render(UserSettings settings, About? about = null, bool focusAbout = false,
        IReadOnlyList<ImportSource>? imports = null, IReadOnlyList<SiteSummary>? sites = null)
    {
        var data = new
        {
            focus = focusAbout ? "sobre" : null,
            chromium = about?.Chromium,
            cefsharp = about?.CefSharp,
            shield = about is null ? null : (about.FullEngine ? "uBlock Origin (adblock-rust) · " : "lista mínima · ") + ListsLine(about.Lists, DateTime.UtcNow),
            updateStatus = about?.UpdateStatus,
            canCheck = about?.CanCheck ?? false,
            token = InternalPages.Token,
            version = Version,
            engine = settings.SearchEngine,
            startup = settings.Startup,
            sleep = settings.SleepMinutes,
            archive = settings.ArchiveDays,
            archiveConfirmed = settings.ArchiveConfirmed,
            httpsFirst = settings.HttpsFirst,
            cookies = settings.BlockThirdPartyCookies,
            mica = settings.Mica,
            autoplay = settings.BlockAutoplay,
            suggest = settings.SearchSuggestions,
            dns = settings.SecureDns,
            clearOnExit = settings.ClearOnExit,
            hideFromCapture = settings.HideFromCapture,
            theme = settings.Theme,
            cleanUrls = settings.CleanUrls,
            notifications = settings.BlockNotificationPrompts,
            imports = (imports ?? []).Select(i => new { id = i.Id, name = i.Name, count = i.Count }),
            sites = (sites ?? []).Select(s => new
            {
                site = s.Site,
                details = new[]
                    {
                        s.ZoomPercent is { } z ? $"zoom {z} %" : null,
                        s.ShieldOff ? "escudo desligado" : null,
                        s.HiddenElements > 0 ? $"{s.HiddenElements} elemento(s) escondido(s)" : null,
                    }.Where(d => d is not null).Concat(s.Flags).Concat(s.Permissions),
            }),
            tabs = settings.VerticalTabs ? "side" : "top",
            density = settings.Density,
            accent = settings.Accent,
            accents = Theme.Accent.Palette.Select(c => new { key = c.Key, name = c.Name, color = Theme.Accent.Hex(c.Color) }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
