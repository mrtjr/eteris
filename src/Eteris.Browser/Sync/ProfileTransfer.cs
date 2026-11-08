using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// «Exportar perfil cifrado» / «Importar perfil cifrado» (Definições › Sincronização): favoritos, Espaços, abas deste
// computador, definições permitidas, decisões do Escudo e notas num ficheiro .eteris, cifrado com um código de
// recuperação novo mostrado UMA vez. Nada sai do computador: o ficheiro vai para Transferências e o utilizador leva-o
// como quiser. É também a prova de ponta a ponta do formato da sincronização (docs/sincronizacao.md).
internal static class ProfileTransfer
{
    public sealed record Exported(string Path, string Code, int Records);

    public sealed record Imported(int Bookmarks, int Settings, int Shield, int Spaces, bool Notes, string From);

    // Valores das definições que viajam (Collections.SyncableSettings), no formato que UserSettings.With aceita.
    public static Dictionary<string, string> SettingValues(UserSettings s)
    {
        static string B(bool v) => v ? "true" : "false";
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = s.SearchEngine, ["startup"] = s.Startup, ["sleep"] = I(s.SleepMinutes), ["httpsFirst"] = B(s.HttpsFirst),
            ["cookies"] = B(s.BlockThirdPartyCookies), ["autoplay"] = B(s.BlockAutoplay), ["suggest"] = B(s.SearchSuggestions),
            ["dns"] = s.SecureDns, ["clearOnExit"] = B(s.ClearOnExit), ["theme"] = s.Theme, ["cleanUrls"] = B(s.CleanUrls),
            ["debounce"] = B(s.SkipTrackingRedirects), ["bounceForget"] = B(s.ForgetBounceTrackers), ["cname"] = B(s.UncloakCname),
            ["notifications"] = B(s.BlockNotificationPrompts), ["verticalTabs"] = B(s.VerticalTabs), ["density"] = s.Density,
            ["accent"] = s.Accent, ["ntTheme"] = s.NewTabTheme, ["ntWallpaper"] = s.NewTabWallpaper, ["ntWallpaperMotion"] = s.NewTabWallpaperMotion,
            ["ntClock"] = B(s.NewTabClock), ["ntTiles"] = B(s.NewTabTiles), ["ntWeek"] = B(s.NewTabWeek), ["ntTips"] = B(s.NewTabTips),
            ["readerTheme"] = s.ReaderTheme, ["readerSize"] = I(s.ReaderSize), ["readerSerif"] = B(s.ReaderSerif),
        };
        // Só o que o utilizador escolheu: o prazo do arquivo e a língua dos sites por omissão dependem deste computador.
        if (s.ArchiveConfirmed) values["archive"] = I(s.ArchiveDays);
        if (s.SiteLanguageChosen) values["siteLanguage"] = s.SiteLanguage;
        return values;
    }

    public static List<SyncRecord> Collect(UserSettings settings, long nowMs)
    {
        var records = new List<SyncRecord>();
        foreach (var mark in AppData.Bookmarks(b => b.All(100_000)))
            records.Add(ProfileArchive.Record(Collections.Bookmarks, mark.Url, new JsonObject { ["title"] = mark.Title }, nowMs));
        foreach (var (key, value) in SettingValues(settings))
            records.Add(ProfileArchive.Record(Collections.Settings, key, new JsonObject { ["value"] = value }, nowMs));

        foreach (var host in Read(() => new ShieldExceptionStore(AppPaths.Database).All()))
            records.Add(ProfileArchive.Record(Collections.Shield, $"off|{host}|", new JsonObject(), nowMs));
        foreach (var host in Read(() => new StrictSiteStore(AppPaths.Database).All()))
            records.Add(ProfileArchive.Record(Collections.Shield, $"strict|{host}|", new JsonObject(), nowMs));
        foreach (var host in AppData.HttpExceptions())
            records.Add(ProfileArchive.Record(Collections.Shield, $"http|{host}|", new JsonObject(), nowMs));
        var hidden = Read(() =>
        {
            var store = new HiddenElementsStore(AppPaths.Database);
            return store.Hosts().Keys.SelectMany(h => store.For(h).Select(selector => $"hide|{h}|{selector}")).ToList();
        });
        foreach (var key in hidden) records.Add(ProfileArchive.Record(Collections.Shield, key, new JsonObject(), nowMs));

        var notes = new NotesStore(Path.Combine(AppPaths.Root, "notas.md")).Load();
        if (notes.Length > 0) records.Add(ProfileArchive.Record(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes }, nowMs));

        if (new SessionStore(Path.Combine(AppPaths.Root, "session.json")).Load() is { } session)
        {
            var order = 0;
            foreach (var space in session.Spaces ?? [])
                records.Add(ProfileArchive.Record(Collections.Spaces, space.Name, new JsonObject
                {
                    ["name"] = space.Name, ["color"] = space.ColorIndex, ["icon"] = space.Icon, ["order"] = order++,
                }, nowMs));
            // As abas abertas deste computador (aparecem noutro como «abas noutros aparelhos»; a importação não as abre).
            var tabs = new JsonArray(session.Urls.Select((url, i) => (JsonNode)new JsonObject
            {
                ["url"] = url, ["title"] = session.Titles is { } t && i < t.Count ? t[i] : null,
            }).ToArray());
            records.Add(ProfileArchive.Record(Collections.Tabs, "este-computador", new JsonObject { ["tabs"] = tabs }, nowMs));
        }
        return records;
    }

    public static Exported Export(string deviceName, DateTime now)
    {
        using var secret = AccountSecret.Create();
        var records = Collect(Data.Settings.Current, new DateTimeOffset(now).ToUnixTimeMilliseconds());
        var bytes = ProfileArchive.Export(records, secret, deviceName, new DateTimeOffset(now).ToUnixTimeMilliseconds());
        var folder = Downloads.DownloadPaths.DownloadsFolder();
        var path = Downloads.DownloadPaths.Unique(folder, FileName(now));
        File.WriteAllBytes(path, bytes);
        return new Exported(path, secret.ToCode(), records.Count);
    }

    public static string FileName(DateTime now) => $"Perfil Éteris {now:yyyy-MM-dd}{ProfileArchive.Extension}";

    // Junta o perfil importado ao deste computador: favoritos, Espaços e decisões do Escudo em falta entram;
    // as definições do ficheiro passam a valer; as notas, se forem diferentes, ficam por baixo das atuais.
    public static Imported Import(byte[] file, string code, Action<IReadOnlyList<(string Name, int Color, string? Icon)>>? addSpaces = null)
    {
        var archive = ProfileArchive.Import(file, code);
        var records = archive.Records.Where(r => !r.Deleted && r.Data is not null).ToList();

        var marks = records.Where(r => r.Collection == Collections.Bookmarks && IsWebAddress(r.Key))
            .Select(r => new Bookmark(r.Key, r.Text("title") ?? string.Empty)).ToList();
        var bookmarks = marks.Count == 0 ? 0 : AppData.ImportBookmarks(marks);

        // Só contam (e só se gravam) as definições que mudam de facto.
        var settings = 0;
        foreach (var r in records.Where(r => r.Collection == Collections.Settings && Collections.SyncableSettings.Contains(r.Key)))
            if (r.Text("value") is { } value && Data.Settings.Current.With(r.Key, value) is { } next && next != Data.Settings.Current
                && Data.Settings.Set(r.Key, value)) settings++;

        var shield = 0;
        foreach (var r in records.Where(r => r.Collection == Collections.Shield))
        {
            var parts = r.Key.Split('|', 3);
            if (parts.Length != 3 || !Collections.ShieldKinds.Contains(parts[0]) || !IsHost(parts[1])) continue;
            switch (parts[0])
            {
                case "off" when AppData.IsShieldOn(parts[1]): AppData.SetShield(parts[1], false); shield++; break;
                case "strict" when !AppData.IsStrict(parts[1]): AppData.SetStrict(parts[1], true); shield++; break;
                case "http" when !AppData.IsHttpException(parts[1]): AppData.SetHttpException(parts[1], true); shield++; break;
                case "hide" when parts[2].Length is > 0 and < 2000 && !AppData.HiddenElements(parts[1]).Contains(parts[2]):
                    AppData.HideElement(parts[1], parts[2]); shield++; break;
            }
        }

        var notesChanged = false;
        if (records.FirstOrDefault(r => r.Collection == Collections.Notes)?.Text("text") is { Length: > 0 } imported)
        {
            var store = new NotesStore(Path.Combine(AppPaths.Root, "notas.md"));
            var current = store.Load();
            var merged = MergeNotes(current, imported, archive.DeviceName, DateTimeOffset.FromUnixTimeMilliseconds(archive.CreatedMs).LocalDateTime);
            if (merged != current)
            {
                store.Save(merged);
                notesChanged = true;
            }
        }

        var spaces = records.Where(r => r.Collection == Collections.Spaces && r.Text("name") is { Length: > 0 })
            .OrderBy(r => r.Data!["order"]?.GetValue<int>() ?? 0)
            .Select(r => (Name: r.Text("name")!, Color: r.Data!["color"]?.GetValue<int>() ?? 0, Icon: r.Text("icon"))).ToList();
        addSpaces?.Invoke(spaces);
        return new Imported(bookmarks, settings, shield, spaces.Count, notesChanged, archive.DeviceName);
    }

    // As notas do ficheiro entram por baixo das atuais, com um título (nada se perde nem se sobrepõe).
    public static string MergeNotes(string current, string imported, string from, DateTime when)
    {
        if (current.Length == 0) return imported;
        if (current.Contains(imported, StringComparison.Ordinal)) return current;
        var label = from.Length > 0 ? $"Notas importadas de «{from}» ({when:dd/MM/yyyy})" : $"Notas importadas ({when:dd/MM/yyyy})";
        return current.TrimEnd('\n') + "\n\n— " + label + " —\n" + imported;
    }

    public static string Summary(Imported r)
    {
        var parts = new List<string>();
        if (r.Bookmarks > 0) parts.Add(r.Bookmarks == 1 ? "1 favorito" : $"{r.Bookmarks} favoritos");
        if (r.Settings > 0) parts.Add(r.Settings == 1 ? "1 definição" : $"{r.Settings} definições");
        if (r.Shield > 0) parts.Add(r.Shield == 1 ? "1 decisão do Escudo" : $"{r.Shield} decisões do Escudo");
        if (r.Spaces > 0) parts.Add(r.Spaces == 1 ? "1 Espaço" : $"{r.Spaces} Espaços");
        if (r.Notes) parts.Add("as notas");
        var from = r.From.Length > 0 ? $" de «{r.From}»" : string.Empty;
        return parts.Count == 0 ? $"Perfil{from} lido: nada de novo para juntar." : $"Perfil{from} importado: {string.Join(", ", parts)}.";
    }

    private static bool IsWebAddress(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool IsHost(string host)
        => host.Length is > 0 and < 254 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']');

    private static IReadOnlyList<string> Read(Func<IReadOnlyList<string>> read)
    {
        try { return read(); }
        catch (Exception) { return []; }
    }
}
