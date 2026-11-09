using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// O perfil do Éteris visto pela sincronização (ILocalProfile): o que se lê e o que se aplica, coleção a coleção.
//
//   favoritos            URL → título
//   definições           só as que viajam (Collections.SyncableSettings; o prazo do arquivo e a língua dos sites só se
//                        o utilizador os escolheu)
//   Escudo por site      off|site| (desligado), strict|site| (modo estrito), http|site| (exceção ao HTTPS primeiro),
//                        hide|site|seletor (elemento escondido), rule|site|domínio (bloqueio por site)
//   Espaços              nome → cor, emoji e ordem (da primeira janela; as abas de cada Espaço ficam em cada computador)
//   notas                o texto das notas
//
// Nunca: histórico, abas abertas, cookies, transferências, palavras-passe (o Éteris não as guarda).
// Ler é só disco (e a lista de Espaços da janela, na thread da interface); aplicar escreve pelos mesmos caminhos que a
// interface usa (AppData, Settings), por isso o resto do Éteris vê as alterações como se fossem feitas aqui.
internal sealed class BrowserProfile : ILocalProfile
{
    public IReadOnlyList<LocalItem> Collect()
    {
        var items = new List<LocalItem>();
        foreach (var mark in AppData.Bookmarks(b => b.All(100_000)))
            items.Add(new LocalItem(Collections.Bookmarks, mark.Url, new JsonObject { ["title"] = mark.Title }));
        foreach (var (key, value) in ProfileTransfer.SettingValues(Data.Settings.Current))
            items.Add(new LocalItem(Collections.Settings, key, new JsonObject { ["value"] = value }));
        foreach (var key in ShieldKeys()) items.Add(new LocalItem(Collections.Shield, key, new JsonObject()));

        var notes = new NotesStore(Path.Combine(AppPaths.Root, "notas.md")).Load();
        if (notes.Length > 0) items.Add(new LocalItem(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes }));

        var order = 0;
        foreach (var (name, color, icon) in Spaces())
            items.Add(new LocalItem(Collections.Spaces, name, SpaceData(name, color, icon, order++)));
        return items;
    }

    public static JsonObject SpaceData(string name, int color, string? icon, int order)
    {
        var data = new JsonObject { ["name"] = name, ["color"] = color, ["order"] = order };
        if (icon is not null) data["icon"] = icon;
        return data;
    }

    // Decisões do Escudo por site, no formato «tipo|site|valor».
    public static List<string> ShieldKeys()
    {
        var keys = new List<string>();
        keys.AddRange(Read(() => new ShieldExceptionStore(AppPaths.Database).All()).Select(h => $"off|{h}|"));
        keys.AddRange(Read(() => new StrictSiteStore(AppPaths.Database).All()).Select(h => $"strict|{h}|"));
        keys.AddRange(AppData.HttpExceptions().Select(h => $"http|{h}|"));
        keys.AddRange(Read(() =>
        {
            var store = new HiddenElementsStore(AppPaths.Database);
            return store.Hosts().Keys.SelectMany(h => store.For(h).Select(selector => $"hide|{h}|{selector}")).ToList();
        }));
        keys.AddRange(Read(() => Shields.SiteRules.All().Select(r => $"rule|{r.Site}|{r.Domain}").ToList()));
        return keys;
    }

    // Espaços da primeira janela normal (na thread da interface); sem janela, os da sessão gravada.
    private static IReadOnlyList<(string Name, int Color, string? Icon)> Spaces()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            try
            {
                if (dispatcher.Invoke(MainWindow.SyncSpaces) is { } live) return live;
            }
            catch (Exception e) when (e is InvalidOperationException or TaskCanceledException) { }
        }
        var session = new SessionStore(Path.Combine(AppPaths.Root, "session.json")).Load();
        return (session?.Spaces ?? []).Select(s => (s.Name, s.ColorIndex, s.Icon)).ToList();
    }

    public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes)
    {
        var applied = new List<LocalChange>();
        foreach (var change in changes)
        {
            bool ok;
            try
            {
                ok = change.Collection switch
                {
                    Collections.Bookmarks => ApplyBookmark(change),
                    Collections.Settings => ApplySetting(change),
                    Collections.Shield => ApplyShield(change),
                    Collections.Notes => OnInterface(() => MainWindow.ApplySyncedNotes(change.Data?["text"]?.GetValue<string>() ?? string.Empty)),
                    Collections.Spaces => OnInterface(() => MainWindow.ApplySyncedSpace(change.Key, change.Data)),
                    _ => false,
                };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException) { ok = false; }
            if (ok) applied.Add(change);
        }
        return applied;
    }

    private static bool ApplyBookmark(LocalChange change)
    {
        if (!Uri.TryCreate(change.Key, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (change.Remove)
        {
            AppData.RemoveBookmark(change.Key);
            return !AppData.IsBookmarked(change.Key);
        }
        var title = change.Data!["title"]?.GetValue<string>() ?? string.Empty;
        if (title.Length > 300) title = title[..300];
        if (AppData.IsBookmarked(change.Key)) AppData.RenameBookmark(change.Key, title);
        else AppData.ImportBookmarks([new Bookmark(change.Key, title)]);
        return AppData.IsBookmarked(change.Key);
    }

    private static bool ApplySetting(LocalChange change)
    {
        // Só as que viajam; apagar uma definição não se aplica (cada computador tem sempre todas).
        if (change.Remove || !Collections.SyncableSettings.Contains(change.Key)) return false;
        if (change.Data!["value"]?.GetValue<string>() is not { } value) return false;
        if (Data.Settings.Current.With(change.Key, value) is not { } next) return false;
        if (next == Data.Settings.Current) return true;
        // Na thread da interface: quem ouve Settings.Changed (tema, abas, densidade…) mexe na janela.
        return OnInterface(() => Data.Settings.Set(change.Key, value));
    }

    private static bool ApplyShield(LocalChange change)
    {
        var parts = change.Key.Split('|', 3);
        if (parts.Length != 3 || !Collections.ShieldKinds.Contains(parts[0]) || !ProfileTransfer.IsHost(parts[1])) return false;
        var (kind, host, value) = (parts[0], parts[1], parts[2]);
        var on = !change.Remove;
        switch (kind)
        {
            case "off":
                AppData.SetShield(host, !on);
                return AppData.IsShieldOn(host) != on;
            case "strict":
                AppData.SetStrict(host, on);
                return AppData.IsStrict(host) == on;
            case "http":
                AppData.SetHttpException(host, on);
                return AppData.IsHttpException(host) == on;
            case "hide":
                if (value.Length is 0 or > 2000) return false;
                if (on) AppData.HideElement(host, value);
                else AppData.UnhideElement(host, value);
                return AppData.HiddenElements(host).Contains(value) == on;
            case "rule":
                if (!ProfileTransfer.IsHost(value)) return false;
                if (on) Shields.SiteRules.Block(host, value, isPrivate: false);
                else Shields.SiteRules.RemoveExact(host, value);
                return Shields.SiteRules.All().Contains((host, value)) == on;
            default:
                return false;
        }
    }

    private static bool OnInterface(Func<bool> action)
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return false;
        try { return dispatcher.Invoke(action); }
        catch (TaskCanceledException) { return false; }
    }

    private static IReadOnlyList<string> Read(Func<IReadOnlyList<string>> read)
    {
        try { return read(); }
        catch (Exception) { return []; }
    }
}
