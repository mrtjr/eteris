using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Eteris.Sync.Google;

namespace Eteris.Browser.Vault;

// Uma palavra-passe na lista ou nas sugestões: só metadados (nunca a palavra-passe).
public sealed record LoginEntry(string Id, string Origin, string Username, OriginMatch Match, JsonObject Meta)
{
    public override string ToString() => $"LoginEntry {Id}";
}

// A fachada do cofre (docs/palavras-passe.md, A2: só armazenamento). Abre-se na primeira vez que é precisa — nunca no
// arranque do Éteris (o arranque não muda). Os nomes dos campos são os da sincronização (desenho, secção 4.1).
public sealed class VaultService : IDisposable
{
    private readonly string _root;
    private readonly ISecretProtector _protector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private VaultStore? _store;

    public VaultService(string root, ISecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _protector = protector;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // O cofre do perfil em uso (--user-data-dir): construído só quando alguém o pede.
    private static readonly Lazy<VaultService> s_profile = new(() => new VaultService(Infrastructure.AppPaths.Root, new Sync.Dpapi(VaultKey.Purpose)));

    public static VaultService Profile => s_profile.Value;

    public bool IsOpen => _store is not null;

    private VaultStore Store
    {
        get
        {
            lock (_gate)
            {
                if (_store is not null) return _store;
                var keyPath = Path.Combine(_root, VaultKey.FileName);
                var dbPath = Path.Combine(_root, VaultStore.FileName);
                if (!File.Exists(keyPath) && File.Exists(dbPath))
                    throw new VaultUnavailableException("A chave do cofre desapareceu: o cofre ficou como estava, sem ser aberto.");
                Directory.CreateDirectory(_root);
                var key = VaultKey.LoadOrCreate(keyPath, _protector);
                try { return _store = new VaultStore(dbPath, key); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _store?.Dispose();
            _store = null;
        }
    }

    private DateTimeOffset Now => _clock();

    // ---- Palavras-passe ----

    // A mesma origem e o mesmo utilizador são o mesmo item: atualiza (a palavra-passe anterior vai para o histórico).
    public string SaveLogin(LoginItem item)
    {
        var origin = Origins.Normalize(item.Origin) ?? throw new ArgumentException("Origem inválida para uma palavra-passe.", nameof(item));
        var username = item.Username ?? string.Empty;
        var id = !string.IsNullOrEmpty(item.Id) && Store.Get(VaultCollection.Logins, item.Id) is not null ? item.Id
            : FindLogin(origin, username) ?? (string.IsNullOrEmpty(item.Id) ? VaultIds.New() : item.Id);
        var old = GetLogin(id);
        var now = Now;
        var passwordChanged = old is not null && old.Password != item.Password;
        var created = old?.Created ?? (item.Created == default ? now : item.Created);
        var changed = old is null ? (item.Changed == default ? now : item.Changed) : passwordChanged ? now : old.Changed;
        var meta = new JsonObject
        {
            ["origin"] = origin,
            ["username"] = username,
            ["created"] = Time(created),
            ["changed"] = Time(changed),
            ["used"] = Time(item.Used),
            ["formAction"] = item.FormAction,
            ["generated"] = item.Generated,
            ["realm"] = item.Realm,
        };
        var secret = new JsonObject { ["password"] = item.Password ?? string.Empty, ["note"] = item.Note };
        Store.Put(VaultCollection.Logins, id, Origins.IsAndroid(origin) ? null : origin, meta, secret, now, keepPrevious: passwordChanged);
        return id;
    }

    public IReadOnlyList<LoginEntry> Logins()
        => Store.List(VaultCollection.Logins).Select(e => ToLoginEntry(e, OriginMatch.None)).ToList();

    // Sugestões para a página: as da origem exata primeiro, depois as do mesmo domínio registável (marcadas).
    public IReadOnlyList<LoginEntry> LoginsFor(string url)
    {
        if (Origins.Normalize(url) is not { } origin || Origins.IsAndroid(origin)) return [];
        var exact = Store.FindByOrigin(VaultCollection.Logins, origin);
        var result = exact.Select(id => Store.Get(VaultCollection.Logins, id)).OfType<VaultEntry>().Select(e => ToLoginEntry(e, OriginMatch.Exact)).ToList();
        if (Origins.Site(origin) is { } site)
            foreach (var id in Store.FindBySite(VaultCollection.Logins, site).Except(exact))
                if (Store.Get(VaultCollection.Logins, id) is { } entry && Origins.Match(Text(entry.Meta, "origin") ?? string.Empty, origin) == OriginMatch.SameSite)
                    result.Add(ToLoginEntry(entry, OriginMatch.SameSite));
        return result;
    }

    // O item inteiro, com a palavra-passe decifrada: só para preencher, mostrar, copiar, exportar ou sincronizar.
    public LoginItem? GetLogin(string id)
    {
        if (Store.Get(VaultCollection.Logins, id) is not { } entry) return null;
        var secret = Store.ReadSecret(VaultCollection.Logins, id) ?? new JsonObject();
        var m = entry.Meta;
        return new LoginItem(id, Text(m, "origin") ?? string.Empty, Text(m, "username") ?? string.Empty, Text(secret, "password") ?? string.Empty,
            Text(secret, "note"), TimeOf(m, "created") ?? entry.Created, TimeOf(m, "changed") ?? entry.Changed, TimeOf(m, "used"),
            Text(m, "formAction"), m["generated"]?.GetValue<bool>() ?? false, Text(m, "realm"));
    }

    public string? PreviousPassword(string id) => Store.ReadPrevious(VaultCollection.Logins, id) is { } prev ? Text(prev, "password") : null;

    // O item com esta origem e este utilizador (para importar), ou null.
    public string? FindLogin(string origin, string username)
    {
        var candidates = Origins.IsAndroid(origin)
            ? Store.List(VaultCollection.Logins).Where(e => Text(e.Meta, "origin") == origin).Select(e => e.Id)
            : Store.FindByOrigin(VaultCollection.Logins, origin);
        foreach (var id in candidates)
            if (Store.Get(VaultCollection.Logins, id) is { } entry && Text(entry.Meta, "origin") == origin && Text(entry.Meta, "username") == username)
                return id;
        return null;
    }

    private static LoginEntry ToLoginEntry(VaultEntry e, OriginMatch match)
        => new(e.Id, Text(e.Meta, "origin") ?? string.Empty, Text(e.Meta, "username") ?? string.Empty, match, e.Meta);

    // ---- «Nunca neste site» ----

    public void AddNeverSave(string url)
    {
        var origin = Origins.Normalize(url) ?? throw new ArgumentException("Origem inválida.", nameof(url));
        if (IsNeverSave(origin)) return;
        Store.Put(VaultCollection.LoginsNever, VaultIds.New(), origin, new JsonObject { ["origin"] = origin, ["created"] = Time(Now) }, null, Now);
    }

    public bool IsNeverSave(string url)
        => Origins.Normalize(url) is { } origin && Store.FindByOrigin(VaultCollection.LoginsNever, origin).Count > 0;

    public IReadOnlyList<NeverSaveSite> NeverSaveSites()
        => Store.List(VaultCollection.LoginsNever).Select(e => new NeverSaveSite(Text(e.Meta, "origin") ?? string.Empty, TimeOf(e.Meta, "created") ?? e.Created)).ToList();

    public void RemoveNeverSave(string url)
    {
        if (Origins.Normalize(url) is not { } origin) return;
        foreach (var id in Store.FindByOrigin(VaultCollection.LoginsNever, origin)) Store.Delete(VaultCollection.LoginsNever, id, Now);
    }

    // ---- Pagamentos, contactos, documentos, viagens ----

    public string Save(CardItem card)
    {
        if (!CardRules.IsValidNumber(card.Number)) throw new ArgumentException("Número de cartão inválido.", nameof(card));
        if (card.ExpMonth is < 1 or > 12 || card.ExpYear is < 2000 or > 2100) throw new ArgumentException("Validade inválida.", nameof(card));
        var number = VaultFields.Digits(card.Number);
        var (id, created) = Identity(VaultCollection.Cards, card.Id, card.Created);
        var meta = new JsonObject
        {
            ["name"] = card.Name,
            ["brand"] = CardRules.Brand(number),
            ["last4"] = number[^4..],
            ["expMonth"] = card.ExpMonth,
            ["expYear"] = card.ExpYear,
            ["nickname"] = card.Nickname,
            ["created"] = Time(created),
            ["used"] = Time(card.Used),
        };
        return Put(VaultCollection.Cards, id, meta, new JsonObject { ["number"] = number });
    }

    public CardItem? GetCard(string id) => Read(VaultCollection.Cards, id, (e, m, s) => new CardItem(id, Text(m, "name"), Text(s, "number") ?? string.Empty,
        m["expMonth"]?.GetValue<int>() ?? 0, m["expYear"]?.GetValue<int>() ?? 0, Text(m, "nickname"), TimeOf(m, "created") ?? e.Created, TimeOf(m, "used")));

    public string Save(IbanItem iban)
    {
        if (!IbanRules.IsValid(iban.Iban)) throw new ArgumentException("IBAN inválido.", nameof(iban));
        var (id, created) = Identity(VaultCollection.Ibans, iban.Id, iban.Created);
        var meta = new JsonObject { ["masked"] = IbanRules.Mask(iban.Iban), ["nickname"] = iban.Nickname, ["created"] = Time(created) };
        return Put(VaultCollection.Ibans, id, meta, new JsonObject { ["iban"] = IbanRules.Normalize(iban.Iban) });
    }

    public IbanItem? GetIban(string id) => Read(VaultCollection.Ibans, id, (e, m, s) =>
        new IbanItem(id, Text(s, "iban") ?? string.Empty, Text(m, "nickname"), TimeOf(m, "created") ?? e.Created));

    public string Save(ContactItem c)
    {
        var (id, created) = Identity(VaultCollection.Contacts, c.Id, c.Created);
        var meta = new JsonObject
        {
            ["label"] = c.Label, ["givenName"] = c.GivenName, ["familyName"] = c.FamilyName, ["organization"] = c.Organization,
            ["street"] = c.Street, ["number"] = c.Number, ["complement"] = c.Complement, ["neighborhood"] = c.Neighborhood,
            ["city"] = c.City, ["region"] = c.Region, ["postalCode"] = c.PostalCode is null ? null : ContactRules.NormalizePostalCode(c.PostalCode, c.Country),
            ["country"] = c.Country, ["email"] = c.Email, ["phone"] = c.Phone, ["created"] = Time(created), ["used"] = Time(c.Used),
        };
        return Put(VaultCollection.Contacts, id, meta, null);
    }

    public ContactItem? GetContact(string id) => Read(VaultCollection.Contacts, id, (e, m, _) => new ContactItem(id, Text(m, "label"), Text(m, "givenName"),
        Text(m, "familyName"), Text(m, "organization"), Text(m, "street"), Text(m, "number"), Text(m, "complement"), Text(m, "neighborhood"),
        Text(m, "city"), Text(m, "region"), Text(m, "postalCode"), Text(m, "country"), Text(m, "email"), Text(m, "phone"),
        TimeOf(m, "created") ?? e.Created, TimeOf(m, "used")));

    public string Save(IdDocItem doc)
    {
        var number = doc.Type == IdDocType.TaxId ? VaultFields.Alnum(doc.Number) : (doc.Number ?? string.Empty).Trim();
        if (number.Length == 0) throw new ArgumentException("Documento sem número.", nameof(doc));
        if (doc.Type == IdDocType.TaxId && !TaxIdRules.IsValid(number, doc.Country)) throw new ArgumentException("Número fiscal inválido.", nameof(doc));
        var (id, _) = Identity(VaultCollection.IdDocs, doc.Id, default);
        var meta = new JsonObject
        {
            ["type"] = IdDocItem.TypeCode(doc.Type), ["name"] = doc.Name, ["country"] = doc.Country, ["issuer"] = doc.Issuer,
            ["issued"] = Day(doc.Issued), ["expires"] = Day(doc.Expires), ["masked"] = VaultFields.MaskTail(number),
        };
        return Put(VaultCollection.IdDocs, id, meta, new JsonObject { ["number"] = number, ["note"] = doc.Note });
    }

    public IdDocItem? GetIdDoc(string id) => Read(VaultCollection.IdDocs, id, (_, m, s) => new IdDocItem(id, IdDocItem.ParseType(Text(m, "type")) ?? IdDocType.NationalId,
        Text(s, "number") ?? string.Empty, Text(m, "name"), Text(m, "country"), Text(m, "issuer"), DayOf(m, "issued"), DayOf(m, "expires"), Text(s, "note")));

    public string Save(TravelItem t)
    {
        var (id, _) = Identity(VaultCollection.Travel, t.Id, default);
        var meta = new JsonObject
        {
            ["type"] = t.Type == TravelType.Vehicle ? "vehicle" : "loyalty", ["make"] = t.Make, ["model"] = t.Model, ["year"] = t.Year,
            ["owner"] = t.Owner, ["plate"] = t.Plate, ["plateRegion"] = t.PlateRegion, ["company"] = t.Company, ["program"] = t.Program,
            ["name"] = t.Name, ["masked"] = string.IsNullOrWhiteSpace(t.Number) ? null : VaultFields.MaskTail(t.Number),
        };
        return Put(VaultCollection.Travel, id, meta, new JsonObject { ["vin"] = t.Vin, ["number"] = t.Number });
    }

    public TravelItem? GetTravel(string id) => Read(VaultCollection.Travel, id, (_, m, s) => new TravelItem(id,
        Text(m, "type") == "vehicle" ? TravelType.Vehicle : TravelType.Loyalty, Text(m, "make"), Text(m, "model"), m["year"]?.GetValue<int>(),
        Text(m, "owner"), Text(m, "plate"), Text(m, "plateRegion"), Text(s, "vin"), Text(m, "company"), Text(m, "program"), Text(s, "number"), Text(m, "name")));

    // ---- Comum ----

    public IReadOnlyList<VaultEntry> List(VaultCollection collection) => Store.List(collection);

    public bool Delete(VaultCollection collection, string id) => Store.Delete(collection, id, Now);

    public IReadOnlyList<(string Id, DateTimeOffset Deleted)> Tombstones(VaultCollection collection) => Store.Tombstones(collection);

    public void Purge(VaultCollection collection, string id) => Store.Purge(collection, id);

    public void EraseAll() => Store.EraseAll();

    private (string Id, DateTimeOffset Created) Identity(VaultCollection collection, string? id, DateTimeOffset created)
    {
        if (!string.IsNullOrEmpty(id) && Store.Get(collection, id) is { } old)
            return (id, TimeOf(old.Meta, "created") ?? old.Created);
        return (string.IsNullOrEmpty(id) ? VaultIds.New() : id, created == default ? Now : created);
    }

    private string Put(VaultCollection collection, string id, JsonObject meta, JsonObject? secret)
    {
        VaultFields.RejectForbidden(meta.Select(p => new KeyValuePair<string, string?>(p.Key, null)));
        if (secret is not null) VaultFields.RejectForbidden(secret.Select(p => new KeyValuePair<string, string?>(p.Key, null)));
        Store.Put(collection, id, null, meta, secret, Now);
        return id;
    }

    private T? Read<T>(VaultCollection collection, string id, Func<VaultEntry, JsonObject, JsonObject, T> build) where T : class
    {
        if (Store.Get(collection, id) is not { } entry) return null;
        var secret = entry.HasSecret ? Store.ReadSecret(collection, id) ?? new JsonObject() : new JsonObject();
        return build(entry, entry.Meta, secret);
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Time(DateTimeOffset? t) => t?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? TimeOf(JsonObject o, string name)
        => Text(o, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? DayOf(JsonObject o, string name)
        => Text(o, name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
