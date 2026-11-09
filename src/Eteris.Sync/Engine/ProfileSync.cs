using System.Text.Json.Nodes;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;

namespace Eteris.Sync.Engine;

// Um dado do perfil local, no formato dos registos (coleção, chave natural, campos). Nas coleções secretas, a chave é a
// chave LOCAL opaca do item (ISecretSource.LocalKey) e os campos são só a impressão {"h": …}.
public sealed record LocalItem(string Collection, string Key, JsonObject Data);

// Uma alteração vinda dos outros aparelhos para aplicar a este: Data null = apagar. Nas coleções secretas, Data é a
// impressão {"h": …} e Secret o conteúdo (chave natural e campos) a gravar no cofre.
public sealed record LocalChange(string Collection, string Key, JsonObject? Data)
{
    public bool Remove => Data is null;

    public SecretItem? Secret { get; init; }
}

// O perfil deste aparelho, visto pela sincronização: o anfitrião (o navegador, ou os testes) sabe ler e escrever os
// seus dados; a sincronização só compara e decide.
public interface ILocalProfile
{
    // O estado atual das coleções sincronizadas (favoritos, Espaços, definições que viajam, Escudo por site, notas e,
    // num perfil que é também ISecretSource, as coleções do cofre — só a chave local e a impressão de cada item, nunca o
    // conteúdo). Nunca abas abertas, histórico nem cookies.
    IReadOnlyList<LocalItem> Collect();

    // Aplica as alterações dos outros aparelhos e devolve as que aplicou de facto. As que não pôde ou não quis aplicar
    // (um Espaço com abas que outro aparelho apagou, um valor que este Éteris não aceita) ficam como estavam aqui.
    IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes);
}

// O que se leu do perfil, e as coleções que NÃO se conseguiram ler (ficheiro bloqueado pelo antivírus, base de dados
// ocupada, sessão ilegível). «Vazio» e «não consegui ler» são coisas diferentes: uma coleção que não se leu não prepara
// remoções nem muda a base nessa ronda — senão uma falha de leitura apagava os favoritos em todos os aparelhos.
public sealed record LocalRead(IReadOnlyList<LocalItem> Items, IReadOnlyCollection<string> Unreadable);

// Um perfil que sabe dizer o que não conseguiu ler (o do navegador). Os outros contam como lidos por inteiro.
public interface ILocalProfileReader : ILocalProfile
{
    LocalRead Read();
}

public sealed record SyncReport(int Sent, int Received, IReadOnlyDictionary<string, string> Pending, bool NeedsUpdate)
{
    // Registos secretos que não abriram ou não se puderam selar nesta ronda (ficaram de fora, sem parar a ronda).
    public int SecretErrors { get; init; }

    // Coleções que fizeram a ronda inteira (ligadas e lidas): a primeira junção delas está feita.
    public IReadOnlyCollection<string> Joined { get; init; } = [];
}

// Como esta ronda trata as coleções: as ligadas neste aparelho e as que acabaram de ser ligadas (primeira junção só
// delas). Por omissão, todas ligadas e nenhuma acabada de ligar.
public sealed record ProfileSyncOptions
{
    public static readonly ProfileSyncOptions Default = new();

    // null = todas. As desligadas não se leem, não se enviam, não se aplicam, os selos delas nunca se abrem, e a base
    // delas fica como estava (desligar não apaga nada, nem aqui nem na conta).
    public IReadOnlySet<string>? Enabled { get; init; }

    // Ligadas agora: «primeira junção» só destas (o que só existe aqui sobe; com a mesma chave, vale o da conta).
    public IReadOnlySet<string>? Fresh { get; init; }

    public bool IsEnabled(string collection) => Enabled is null || Enabled.Contains(collection);
}

// Liga o perfil local ao motor. Guarda uma «base» (como o perfil local estava no fim da última sincronização, só os
// campos conhecidos): o que mudou desde então neste aparelho é uma alteração local (vai para os outros); o que o motor
// tem de diferente depois de receber é uma alteração dos outros (aplica-se aqui). Assim nenhum gancho em cada sítio
// que escreve dados é preciso para não perder nada: a comparação vê tudo.
//
//   1. alterações locais = perfil agora ≠ base  → Put / Delete no motor (os campos desconhecidos do registo ficam)
//   2. receber (Pull)
//   3. o que o motor tem ≠ perfil → aplicar aqui, salvo o que o utilizador mudou entretanto (fica para a próxima ronda)
//   4. base nova = perfil com o que foi aplicado; depois, quem chama envia (Push)
//
// Primeira vez neste aparelho (base null — conta criada agora, ou aparelho que acabou de entrar) ou coleção acabada de
// ligar: nada se perde. O que só existe aqui sobe; o que já existe na conta vale (definições, títulos, Espaços,
// palavras-passe — a deste computador vai para o histórico do cofre); as notas diferentes juntam-se, com as deste
// computador por baixo de um título.
//
// Coleções secretas (o cofre: docs/sincronizacao.md, «Coleções secretas»): o perfil dá só a impressão de cada item; a
// base guarda essa impressão, nunca o conteúdo nem o site. Quando a impressão muda, pede o conteúdo a ISecretSource
// (só esse item se decifra), sela-o e faz PutSecret. No sentido contrário, abre os selos das coleções ligadas e entrega
// o conteúdo ao perfil, que o volta a cifrar com a chave local.
public static class ProfileSync
{
    // Campos que este Éteris conhece em cada coleção (os outros, de um Éteris mais recente, são preservados no motor).
    // Nas secretas, o perfil e a base só têm a impressão («h»).
    public static readonly IReadOnlyDictionary<string, string[]> Fields = BuildFields();

    private static Dictionary<string, string[]> BuildFields()
    {
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Collections.Bookmarks] = ["title"],
            [Collections.Settings] = ["value"],
            [Collections.Shield] = [],
            [Collections.Spaces] = ["name", "color", "icon", "order"],
            [Collections.Notes] = ["text"],
        };
        foreach (var secret in Collections.Secret) fields[secret] = ["h"];
        return fields;
    }

    public static string Id(string collection, string key) => collection + "\n" + key;

    // A impressão de um item secreto no formato do perfil.
    public static JsonObject Print(string fingerprint) => new() { ["h"] = fingerprint };

    // Forma canónica (só os campos conhecidos, por ordem, sem nulos): duas formas iguais = o mesmo dado.
    public static string? Canonical(string collection, JsonObject? data)
    {
        if (data is null || !Fields.TryGetValue(collection, out var fields)) return null;
        var canonical = new JsonObject();
        foreach (var field in fields)
            if (data[field] is { } value) canonical[field] = value.DeepClone();
        return canonical.ToJsonString();
    }

    public static string? Canonical(LocalItem? item) => item is null ? null : Canonical(item.Collection, item.Data);

    private static LocalRead Read(ILocalProfile local) => local is ILocalProfileReader reader ? reader.Read() : new LocalRead(local.Collect(), []);

    private static string CollectionOf(string id) => id[..id.IndexOf('\n', StringComparison.Ordinal)];

    private static Dictionary<string, LocalItem> Index(IEnumerable<LocalItem> items, ProfileSyncOptions options)
    {
        var index = new Dictionary<string, LocalItem>(StringComparer.Ordinal);
        foreach (var item in items)
            if (Fields.ContainsKey(item.Collection) && options.IsEnabled(item.Collection)) index[Id(item.Collection, item.Key)] = item;
        return index;
    }

    // O que o motor tem, no formato do perfil local (o que este aparelho devia ter).
    public static Dictionary<string, LocalItem> Desired(SyncEngine engine) => Account(engine, null, ProfileSyncOptions.Default).Items;

    // O que a conta tem, já com os selos abertos (só das coleções ligadas, e só com um perfil que tenha cofre).
    private sealed class AccountView
    {
        public Dictionary<string, LocalItem> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SecretItem> Secrets { get; } = new(StringComparer.Ordinal);
        // Coleções com algum selo que não abriu: nesta ronda não se apaga nada delas aqui (o registo que falta pode ser
        // só um que este Éteris não consegue abrir — de um Éteris mais recente, por exemplo).
        public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);
        // Coleções que o cofre deste computador não deixou ver (não abre agora): ilegíveis nesta ronda.
        public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);
        public int Errors { get; set; }
    }

    private static AccountView Account(SyncEngine engine, ISecretSource? secrets, ProfileSyncOptions options)
    {
        var view = new AccountView();
        foreach (var record in engine.State.Records.Values)
        {
            if (record.Deleted || record.Data is null || record.Collection == Collections.Notes || !Fields.TryGetValue(record.Collection, out var fields)
                || !options.IsEnabled(record.Collection)) continue;
            if (Collections.IsSecret(record.Collection))
            {
                if (secrets is null) continue;
                if (engine.OpenSecret(record) is not { } item || !SecretSchemas.IsValid(record.Collection, item.Data))
                {
                    view.Errors++;
                    view.Broken.Add(record.Collection);
                    continue;
                }
                if (view.Unavailable.Contains(record.Collection)) continue;
                string key, print;
                try
                {
                    key = secrets.LocalKey(record.Collection, item.NaturalKey);
                    print = secrets.Fingerprint(record.Collection, item);
                }
                catch (SecretSourceUnavailableException)
                {
                    view.Unavailable.Add(record.Collection);
                    view.Broken.Add(record.Collection);
                    continue;
                }
                var id = Id(record.Collection, key);
                view.Items[id] = new LocalItem(record.Collection, key, Print(print));
                view.Secrets[id] = item;
                continue;
            }
            var data = new JsonObject();
            foreach (var field in fields)
                if (record.Data[field] is { } value) data[field] = value.DeepClone();
            view.Items[Id(record.Collection, record.Key)] = new LocalItem(record.Collection, record.Key, data);
        }
        if (options.IsEnabled(Collections.Notes) && engine.Notes is { Length: > 0 } notes)
            view.Items[Id(Collections.Notes, Collections.NotesKey)] = new LocalItem(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes });
        return view;
    }

    private static bool IsFresh(string collection, IReadOnlyDictionary<string, string>? baseline, ProfileSyncOptions options)
        => baseline is null || options.Fresh?.Contains(collection) == true;

    // Alterações locais desde a base (sem rede nem segredos decifrados): o agendador só sincroniza depois de uma
    // alteração se houver alguma.
    public static int CountLocalChanges(ILocalProfile local, IReadOnlyDictionary<string, string>? baseline)
        => CountLocalChanges(local, baseline, ProfileSyncOptions.Default);

    public static int CountLocalChanges(ILocalProfile local, IReadOnlyDictionary<string, string>? baseline, ProfileSyncOptions options)
    {
        var read = Read(local);
        var now = Index(read.Items, options);
        if (baseline is null) return now.Count;
        return now.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal).Count(id =>
        {
            var collection = CollectionOf(id);
            if (read.Unreadable.Contains(collection) || !options.IsEnabled(collection)) return false;
            if (IsFresh(collection, baseline, options)) return now.ContainsKey(id);
            return Canonical(now.GetValueOrDefault(id)) != baseline.GetValueOrDefault(id);
        });
    }

    public static Task<(SyncReport Report, Dictionary<string, string> Baseline)> RunAsync(SyncEngine engine, ILocalProfile local,
        IReadOnlyDictionary<string, string>? baseline, string deviceName, CancellationToken cancellationToken = default)
        => RunAsync(engine, local, baseline, deviceName, ProfileSyncOptions.Default, cancellationToken);

    public static async Task<(SyncReport Report, Dictionary<string, string> Baseline)> RunAsync(SyncEngine engine, ILocalProfile local,
        IReadOnlyDictionary<string, string>? baseline, string deviceName, ProfileSyncOptions options, CancellationToken cancellationToken = default)
    {
        var secrets = local as ISecretSource;
        var first = Read(local);
        var before = Index(first.Items, options);
        // Coleções que não se leram nesta ronda: nem remoções, nem alterações aplicadas, e a base delas fica igual.
        var unreadable = new HashSet<string>(first.Unreadable, StringComparer.Ordinal);
        // Itens secretos que não se conseguiram selar (ou ler) agora: a base deles fica como estava (tenta outra vez).
        var failed = new HashSet<string>(StringComparer.Ordinal);
        // Coleções secretas que o cofre deste computador não deixou ver a meio da ronda: como as ilegíveis, a base delas
        // fica como estava.
        var lost = new HashSet<string>(StringComparer.Ordinal);
        var errors = 0;
        var sent = 0;
        if (baseline is not null)
        {
            // Para apagar um item secreto é preciso saber que registo da conta lhe corresponde (pela chave local).
            AccountView? current = null;
            foreach (var id in before.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                var collection = CollectionOf(id);
                if (unreadable.Contains(collection) || !options.IsEnabled(collection) || IsFresh(collection, baseline, options)) continue;
                var now = before.GetValueOrDefault(id);
                if (Canonical(now) == baseline.GetValueOrDefault(id)) continue;
                if (now is not null)
                {
                    if (Stage(engine, now, secrets)) sent++;
                    else
                    {
                        failed.Add(id);
                        errors++;
                    }
                }
                else
                {
                    if (Collections.IsSecret(collection))
                    {
                        current ??= Account(engine, secrets, options);
                        if (current.Unavailable.Contains(collection))
                        {
                            lost.Add(collection);
                            continue;
                        }
                    }
                    StageRemoval(engine, id, current);
                    sent++;
                }
            }
        }

        var pull = await engine.PullAsync(cancellationToken).ConfigureAwait(false);

        // Primeira junção (tudo, ou só as coleções acabadas de ligar): junta sem perder nada.
        if (baseline is null || options.Fresh is { Count: > 0 })
        {
            var account = Account(engine, secrets, options);
            lost.UnionWith(account.Unavailable);
            unreadable.UnionWith(account.Unavailable);
            foreach (var (id, item) in before)
            {
                if (unreadable.Contains(item.Collection) || !IsFresh(item.Collection, baseline, options)) continue;
                if (!account.Items.TryGetValue(id, out var remote))
                {
                    if (Stage(engine, item, secrets)) sent++;
                    else
                    {
                        failed.Add(id);
                        errors++;
                    }
                }
                else if (item.Collection == Collections.Notes && Canonical(remote) != Canonical(item))
                {
                    engine.SetNotes(JoinNotes(remote.Data["text"]?.GetValue<string>() ?? string.Empty, item.Data["text"]?.GetValue<string>() ?? string.Empty, deviceName));
                    sent++;
                }
            }
        }

        // Aplicar aqui o que a conta tem de diferente, salvo o que o utilizador mudou durante esta ronda.
        var desired = Account(engine, secrets, options);
        errors += desired.Errors;
        var second = Read(local);
        var during = Index(second.Items, options);
        unreadable.UnionWith(second.Unreadable);
        lost.UnionWith(desired.Unavailable);
        unreadable.UnionWith(lost);
        var changes = new List<LocalChange>();
        foreach (var id in desired.Items.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal))
        {
            var collection = CollectionOf(id);
            if (unreadable.Contains(collection) || failed.Contains(id)) continue;
            var want = desired.Items.GetValueOrDefault(id);
            var have = before.GetValueOrDefault(id);
            if (Canonical(want) == Canonical(have)) continue;
            if (Canonical(during.GetValueOrDefault(id)) != Canonical(have)) continue;
            // Um selo desta coleção não abriu: o que falta pode ser ele, por isso nada se apaga dela aqui nesta ronda.
            if (want is null && desired.Broken.Contains(collection)) continue;
            var (_, key) = Split(id);
            changes.Add(new LocalChange(collection, key, want?.Data.DeepClone().AsObject()) { Secret = desired.Secrets.GetValueOrDefault(id) });
        }
        IReadOnlyCollection<LocalChange> applied = changes.Count == 0 ? [] : local.Apply(changes);

        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, item) in before)
        {
            if (!failed.Contains(id)) next[id] = Canonical(item)!;
            else if (baseline?.GetValueOrDefault(id) is { } old) next[id] = old;
        }
        // O que não se leu, e o que está desligado neste aparelho, fica na base como estava (a ronda seguinte, já com a
        // leitura a funcionar ou com a coleção ligada outra vez, compara com ela).
        if (baseline is not null)
            foreach (var (id, value) in baseline)
            {
                var collection = CollectionOf(id);
                if (first.Unreadable.Contains(collection) || lost.Contains(collection) || !options.IsEnabled(collection)) next[id] = value;
            }
        foreach (var change in applied)
        {
            var id = Id(change.Collection, change.Key);
            if (change.Data is null) next.Remove(id);
            else next[id] = Canonical(change.Collection, change.Data)!;
        }

        var joined = Fields.Keys.Where(c => options.IsEnabled(c) && !unreadable.Contains(c)).ToList();
        // O envio (PushAsync) fica para quem chama, depois de guardar a base: um envio que falha não faz das alterações
        // recebidas e aplicadas aqui «alterações locais» na ronda seguinte.
        return (new SyncReport(sent, applied.Count, pull.Pending, pull.NeedsUpdate) { SecretErrors = errors, Joined = joined }, next);
    }

    private static (string Collection, string Key) Split(string id)
    {
        var at = id.IndexOf('\n', StringComparison.Ordinal);
        return (id[..at], id[(at + 1)..]);
    }

    // Prepara o envio de um item local. false = um item secreto que não se leu ou não se pôde selar (fica de fora).
    private static bool Stage(SyncEngine engine, LocalItem item, ISecretSource? secrets)
    {
        if (item.Collection == Collections.Notes)
        {
            var text = item.Data["text"]?.GetValue<string>() ?? string.Empty;
            if (text != engine.Notes) engine.SetNotes(text);
            return true;
        }
        if (Collections.IsSecret(item.Collection))
        {
            // Só agora, e só este item, se decifra.
            if (secrets?.ReadSecret(item.Collection, item.Key) is not { } content || !SecretSchemas.IsValid(item.Collection, content.Data)) return false;
            // Na conta há um registo vivo com esta chave que este Éteris não abre (de um Éteris mais recente, por
            // exemplo): não se escreve por cima dele.
            if (engine.Get(item.Collection, engine.SecretKey(item.Collection, content.NaturalKey)) is { Deleted: false } current
                && engine.OpenSecret(current) is null) return false;
            try { engine.PutSecret(item.Collection, content); }
            catch (ArgumentException) { return false; } // campo proibido ou grande demais: não sai
            return true;
        }
        // Parte do registo que já existe: os campos que este Éteris não conhece seguem tal e qual.
        var data = engine.Get(item.Collection, item.Key) is { Deleted: false, Data: { } existing } ? existing.DeepClone().AsObject() : new JsonObject();
        foreach (var field in Fields[item.Collection])
        {
            if (item.Data[field] is { } value) data[field] = value.DeepClone();
            else data.Remove(field);
        }
        engine.Put(item.Collection, item.Key, data);
        return true;
    }

    private static void StageRemoval(SyncEngine engine, string id, AccountView? account)
    {
        var (collection, key) = Split(id);
        if (collection == Collections.Notes)
        {
            if (engine.Notes.Length > 0) engine.SetNotes(string.Empty);
            return;
        }
        if (Collections.IsSecret(collection))
        {
            if (account?.Secrets.GetValueOrDefault(id) is { } secret) engine.DeleteSecret(collection, secret.NaturalKey);
            return;
        }
        if (engine.Get(collection, key) is { Deleted: false }) engine.Delete(collection, key);
    }

    // Notas diferentes ao entrar num aparelho: as da conta ficam por cima e as deste computador por baixo, com um
    // título (nada se perde nem se sobrepõe).
    public static string JoinNotes(string account, string local, string deviceName)
    {
        if (local.Length == 0 || account.Contains(local, StringComparison.Ordinal)) return account;
        if (account.Length == 0 || local.Contains(account, StringComparison.Ordinal)) return local;
        var label = deviceName.Length > 0 ? $"Notas de «{deviceName}»" : "Notas deste computador";
        return account.TrimEnd('\n') + "\n\n— " + label + " —\n" + local;
    }
}
