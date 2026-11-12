using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// O que o BrowserProfile lê e escreve no perfil. A implementação viva (LiveProfileData) chama AppData, Settings, SiteRules e a
// janela, que são estáticos e ficam presos ao perfil do processo; os testes põem um duplo em memória e provam as regras do
// BrowserProfile (o que é ilegível, o que se valida, o que conta como aplicado) sem tocar no perfil verdadeiro.
internal interface IProfileData
{
    IReadOnlyList<Bookmark> ReadBookmarks();                              // lança se não se leu
    UserSettings Settings { get; }
    List<string>? ShieldKeys();                                           // null = não se leu
    string? Notes();                                                      // null = não se leu
    IReadOnlyList<(string Name, int Color, string? Icon)>? Spaces();      // null = não se sabe
    bool IsBookmarked(string url);
    void AddBookmark(string url, string title);
    void RenameBookmark(string url, string title);
    void RemoveBookmark(string url);
    bool SetSetting(string key, string value);                            // true = ficou aplicada
    bool HasShield(string kind, string host, string value);
    void SetShield(string kind, string host, string value, bool on);
    bool ApplyNotes(string text);                                         // false = não se aplicou agora
    bool ApplySpace(string name, JsonObject? data);                       // data null = remover
}

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
// Este tipo só fala com o perfil por IProfileData (as chamadas vivas estão em LiveProfileData, no fim do ficheiro).
internal sealed class BrowserProfile(IProfileData? data = null) : ILocalProfileReader, ILocalNotices
{
    private readonly IProfileData _data = data ?? new LiveProfileData();
    private readonly Lock _gate = new();
    private string? _notice;

    public IReadOnlyList<LocalItem> Collect() => Read().Items;

    public LocalRead Read()
    {
        var items = new List<LocalItem>();
        var unreadable = new List<string>();
        try
        {
            foreach (var mark in _data.ReadBookmarks())
                items.Add(new LocalItem(Collections.Bookmarks, mark.Url, new JsonObject { ["title"] = mark.Title }));
        }
        catch (Exception) { unreadable.Add(Collections.Bookmarks); }
        foreach (var (key, value) in ProfileTransfer.SettingValues(_data.Settings))
            items.Add(new LocalItem(Collections.Settings, key, new JsonObject { ["value"] = value }));
        if (_data.ShieldKeys() is { } shield) foreach (var key in shield) items.Add(new LocalItem(Collections.Shield, key, new JsonObject()));
        else unreadable.Add(Collections.Shield);

        if (_data.Notes() is not { } notes) unreadable.Add(Collections.Notes);
        else if (notes.Length > 0) items.Add(new LocalItem(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes }));

        if (_data.Spaces() is not { } spaces) unreadable.Add(Collections.Spaces);
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

    // O que esta alteração vinda da conta baixa na proteção deste computador (a descrição), ou null. Avisa-se, não se retém:
    // a alteração aplica-se na mesma (reter deixava este computador diferente da conta).
    internal static string? Weakening(UserSettings current, LocalChange change)
    {
        if (change.Collection == Collections.Settings)
            return !change.Remove && change.Data!["value"] is JsonValue v && v.TryGetValue<string>(out var value)
                ? ProfileTransfer.WeakensSetting(current, change.Key, value)
                : null;
        if (change.Collection != Collections.Shield) return null;
        var parts = change.Key.Split('|', 3);
        if (parts.Length != 3) return null;
        if (change.Remove) return parts[0] == "strict" ? $"Desligar o modo estrito em {parts[1]}" : null;
        return ProfileTransfer.WeakensShield(parts[0], parts[1]);
    }

    // O aviso das alterações que baixaram a proteção, para o estado da sincronização (lido no fim de cada ronda).
    public string? TakeNotice()
    {
        lock (_gate)
        {
            var notice = _notice;
            _notice = null;
            return notice;
        }
    }

    public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes)
    {
        var applied = new List<LocalChange>();
        var lowered = new List<string>();
        foreach (var change in changes)
        {
            bool ok;
            var weaker = Weakening(_data.Settings, change);
            try
            {
                ok = change.Collection switch
                {
                    Collections.Bookmarks => ApplyBookmark(change),
                    Collections.Settings => ApplySetting(change),
                    Collections.Shield => ApplyShield(change),
                    Collections.Notes => _data.ApplyNotes(change.Data?["text"]?.GetValue<string>() ?? string.Empty),
                    Collections.Spaces => _data.ApplySpace(change.Key, change.Data),
                    _ => false,
                };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException) { ok = false; }
            if (!ok) continue;
            applied.Add(change);
            if (weaker is not null && !lowered.Contains(weaker)) lowered.Add(weaker);
        }
        if (lowered.Count > 0)
            lock (_gate)
                _notice = "Noutro computador da conta baixou-se a proteção: " + string.Join("; ", lowered.Take(5))
                          + (lowered.Count > 5 ? $" e mais {lowered.Count - 5}" : string.Empty)
                          + ". Se não foi você, reveja as proteções e os aparelhos ligados.";
        return applied;
    }

    private bool ApplyBookmark(LocalChange change)
    {
        if (!Uri.TryCreate(change.Key, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (change.Remove)
        {
            _data.RemoveBookmark(change.Key);
            return !_data.IsBookmarked(change.Key);
        }
        var title = change.Data!["title"]?.GetValue<string>() ?? string.Empty;
        if (title.Length > 300) title = title[..300];
        if (_data.IsBookmarked(change.Key)) _data.RenameBookmark(change.Key, title);
        else _data.AddBookmark(change.Key, title);
        return _data.IsBookmarked(change.Key);
    }

    private bool ApplySetting(LocalChange change)
    {
        // Só as que viajam; apagar uma definição não se aplica (cada computador tem sempre todas).
        if (change.Remove || !Collections.SyncableSettings.Contains(change.Key)) return false;
        if (change.Data!["value"]?.GetValue<string>() is not { } value) return false;
        if (_data.Settings.With(change.Key, value) is not { } next) return false;
        if (next == _data.Settings) return true;
        return _data.SetSetting(change.Key, value);
    }

    private bool ApplyShield(LocalChange change)
    {
        var parts = change.Key.Split('|', 3);
        if (parts.Length != 3 || !Collections.ShieldKinds.Contains(parts[0]) || !ProfileTransfer.IsHost(parts[1])) return false;
        var (kind, host, value) = (parts[0], parts[1], parts[2]);
        var on = !change.Remove;
        switch (kind)
        {
            case "off":
            case "strict":
            case "http":
                break;
            case "hide":
                if (value.Length is 0 or > 2000) return false;
                break;
            case "rule":
                if (!ProfileTransfer.IsHost(value)) return false;
                break;
            default:
                return false;
        }
        _data.SetShield(kind, host, value, on);
        return _data.HasShield(kind, host, value) == on;
    }
}

// O perfil verdadeiro: as chamadas ao AppData, às Definições, às regras por site e à janela, uma a uma.
internal sealed class LiveProfileData : IProfileData
{
    public IReadOnlyList<Bookmark> ReadBookmarks() => AppData.ReadBookmarks(b => b.All(100_000));

    public UserSettings Settings => Data.Settings.Current;

    public List<string>? ShieldKeys() => BrowserProfile.ShieldKeys();

    public string? Notes() => new NotesStore(Path.Combine(AppPaths.Root, "notas.md")).TryLoad();

    // Espaços da primeira janela normal (na thread da interface); sem janela, os da sessão gravada. null = nem janela
    // nem sessão legível (não se sabe que Espaços há).
    public IReadOnlyList<(string Name, int Color, string? Icon)>? Spaces()
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

    public bool IsBookmarked(string url) => AppData.IsBookmarked(url);

    public void AddBookmark(string url, string title) => AppData.ImportBookmarks([new Bookmark(url, title)]);

    public void RenameBookmark(string url, string title) => AppData.RenameBookmark(url, title);

    public void RemoveBookmark(string url) => AppData.RemoveBookmark(url);

    // Na thread da interface: quem ouve Settings.Changed (tema, abas, densidade…) mexe na janela.
    public bool SetSetting(string key, string value) => OnInterface(() => Data.Settings.Set(key, value));

    public bool HasShield(string kind, string host, string value) => kind switch
    {
        "off" => !AppData.IsShieldOn(host),
        "strict" => AppData.IsStrict(host),
        "http" => AppData.IsHttpException(host),
        "hide" => AppData.HiddenElements(host).Contains(value),
        "rule" => Shields.SiteRules.All().Contains((host, value)),
        _ => false,
    };

    public void SetShield(string kind, string host, string value, bool on)
    {
        switch (kind)
        {
            case "off":
                AppData.SetShield(host, !on);
                break;
            case "strict":
                AppData.SetStrict(host, on);
                break;
            case "http":
                AppData.SetHttpException(host, on);
                break;
            case "hide":
                if (on) AppData.HideElement(host, value);
                else AppData.UnhideElement(host, value);
                break;
            case "rule":
                if (on) Shields.SiteRules.Block(host, value, isPrivate: false);
                else Shields.SiteRules.RemoveExact(host, value);
                break;
        }
    }

    public bool ApplyNotes(string text) => OnInterface(() => MainWindow.ApplySyncedNotes(text));

    public bool ApplySpace(string name, JsonObject? data) => OnInterface(() => MainWindow.ApplySyncedSpace(name, data));

    private static bool OnInterface(Func<bool> action)
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return false;
        try { return dispatcher.Invoke(action); }
        catch (TaskCanceledException) { return false; }
    }
}
