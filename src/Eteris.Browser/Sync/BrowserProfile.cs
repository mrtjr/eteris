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
// Nunca: histórico, abas abertas, cookies, transferências. As palavras-passe e o preenchimento automático vêm do cofre
// (Vault/VaultSyncProfile, juntos em Vault/ProfileWithVault), com uma segunda cifra por registo.
// Ler é só disco (e a lista de Espaços da janela, na thread da interface); aplicar escreve pelos mesmos caminhos que a
// interface usa (AppData, Settings), por isso o resto do Éteris vê as alterações como se fossem feitas aqui.
// Uma coleção que não se consegue ler (base de dados ocupada, notas bloqueadas pelo antivírus, sem janela nem sessão
// gravada) vai em LocalRead.Unreadable, e não como vazia: vazia queria dizer «apaguei tudo» nos outros aparelhos.
internal sealed class BrowserProfile : ILocalProfileReader
{
    public IReadOnlyList<LocalItem> Collect() => Read().Items;

    public LocalRead Read()
    {
        var items = new List<LocalItem>();
        var unreadable = new List<string>();
        try
        {
            foreach (var mark in AppData.ReadBookmarks(b => b.All(100_000)))
                items.Add(new LocalItem(Collections.Bookmarks, mark.Url, new JsonObject { ["title"] = mark.Title }));
        }
        catch (Exception) { unreadable.Add(Collections.Bookmarks); }
        foreach (var (key, value) in ProfileTransfer.SettingValues(Data.Settings.Current))
            items.Add(new LocalItem(Collections.Settings, key, new JsonObject { ["value"] = value }));
        if (ShieldKeys() is { } shield) foreach (var key in shield) items.Add(new LocalItem(Collections.Shield, key, new JsonObject()));
        else unreadable.Add(Collections.Shield);

        if (new NotesStore(Path.Combine(AppPaths.Root, "notas.md")).TryLoad() is not { } notes) unreadable.Add(Collections.Notes);
        else if (notes.Length > 0) items.Add(new LocalItem(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes }));

        if (Spaces() is not { } spaces) unreadable.Add(Collections.Spaces);
        else
        {
            var order = 0;
            foreach (var (name, color, icon) in spaces)
                items.Add(new LocalItem(Collections.Spaces, name, SpaceData(name, color, icon, order++)));
        }
        return new LocalRead(items, unreadable);
    }

    public static JsonObject SpaceData(string name, int color, string? icon, int order)
    {
        var data = new JsonObject { ["name"] = name, ["color"] = color, ["order"] = order };
        if (icon is not null) data["icon"] = icon;
        return data;
    }

    // Decisões do Escudo por site, no formato «tipo|site|valor». null = uma das listas não se leu (base de dados
    // ocupada ou danificada): o Escudo inteiro fica de fora dessa ronda.
    public static List<string>? ShieldKeys()
    {
        try
        {
            var keys = new List<string>();
            keys.AddRange(new ShieldExceptionStore(AppPaths.Database).All().Select(h => $"off|{h}|"));
            keys.AddRange(new StrictSiteStore(AppPaths.Database).All().Select(h => $"strict|{h}|"));
            keys.AddRange(AppData.ReadHttpExceptions().Select(h => $"http|{h}|"));
            var hidden = new HiddenElementsStore(AppPaths.Database);
            keys.AddRange(hidden.Hosts().Keys.SelectMany(h => hidden.For(h).Select(selector => $"hide|{h}|{selector}")));
            keys.AddRange(Shields.SiteRules.ReadAll().Select(r => $"rule|{r.Site}|{r.Domain}"));
            return keys;
        }
        catch (Exception) { return null; }
    }

    // Espaços da primeira janela normal (na thread da interface); sem janela, os da sessão gravada. null = nem janela
    // nem sessão legível (não se sabe que Espaços há).
    private static IReadOnlyList<(string Name, int Color, string? Icon)>? Spaces()
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
        return session is null ? null : (session.Spaces ?? []).Select(s => (s.Name, s.ColorIndex, s.Icon)).ToList();
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
}
