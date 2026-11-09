using System.Text.Json.Nodes;
using Eteris.Sync.Model;

namespace Eteris.Sync.Engine;

// Um dado do perfil local, no formato dos registos (coleção, chave natural, campos).
public sealed record LocalItem(string Collection, string Key, JsonObject Data);

// Uma alteração vinda dos outros aparelhos para aplicar a este: Data null = apagar.
public sealed record LocalChange(string Collection, string Key, JsonObject? Data)
{
    public bool Remove => Data is null;
}

// O perfil deste aparelho, visto pela sincronização: o anfitrião (o navegador, ou os testes) sabe ler e escrever os
// seus dados; a sincronização só compara e decide.
public interface ILocalProfile
{
    // O estado atual das coleções sincronizadas (favoritos, Espaços, definições que viajam, Escudo por site, notas).
    // Nunca abas abertas, histórico, cookies nem palavras-passe.
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

public sealed record SyncReport(int Sent, int Received, IReadOnlyDictionary<string, string> Pending, bool NeedsUpdate);

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
// Primeira vez neste aparelho (base null — conta criada agora, ou aparelho que acabou de entrar): nada se perde. O que
// só existe aqui sobe; o que já existe na conta vale (definições, títulos, Espaços); as notas diferentes juntam-se, com
// as deste computador por baixo de um título.
public static class ProfileSync
{
    // Campos que este Éteris conhece em cada coleção (os outros, de um Éteris mais recente, são preservados no motor).
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Collections.Bookmarks] = ["title"],
        [Collections.Settings] = ["value"],
        [Collections.Shield] = [],
        [Collections.Spaces] = ["name", "color", "icon", "order"],
        [Collections.Notes] = ["text"],
    };

    public static string Id(string collection, string key) => collection + "\n" + key;

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

    private static Dictionary<string, LocalItem> Index(IEnumerable<LocalItem> items)
    {
        var index = new Dictionary<string, LocalItem>(StringComparer.Ordinal);
        foreach (var item in items)
            if (Fields.ContainsKey(item.Collection)) index[Id(item.Collection, item.Key)] = item;
        return index;
    }

    // O que o motor tem, no formato do perfil local (o que este aparelho devia ter).
    public static Dictionary<string, LocalItem> Desired(SyncEngine engine)
    {
        var items = new List<LocalItem>();
        foreach (var record in engine.State.Records.Values)
        {
            if (record.Deleted || record.Data is null || record.Collection == Collections.Notes || !Fields.TryGetValue(record.Collection, out var fields)) continue;
            var data = new JsonObject();
            foreach (var field in fields)
                if (record.Data[field] is { } value) data[field] = value.DeepClone();
            items.Add(new LocalItem(record.Collection, record.Key, data));
        }
        if (engine.Notes is { Length: > 0 } notes) items.Add(new LocalItem(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = notes }));
        return Index(items);
    }

    // Alterações locais desde a base (sem rede): o agendador só sincroniza depois de uma alteração se houver alguma.
    public static int CountLocalChanges(ILocalProfile local, IReadOnlyDictionary<string, string>? baseline)
    {
        var read = Read(local);
        var now = Index(read.Items);
        if (baseline is null) return now.Count;
        return now.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal)
            .Count(id => !read.Unreadable.Contains(CollectionOf(id)) && Canonical(now.GetValueOrDefault(id)) != baseline.GetValueOrDefault(id));
    }

    public static async Task<(SyncReport Report, Dictionary<string, string> Baseline)> RunAsync(SyncEngine engine, ILocalProfile local,
        IReadOnlyDictionary<string, string>? baseline, string deviceName, CancellationToken cancellationToken = default)
    {
        var first = Read(local);
        var before = Index(first.Items);
        // Coleções que não se leram nesta ronda: nem remoções, nem alterações aplicadas, e a base delas fica igual.
        var unreadable = new HashSet<string>(first.Unreadable, StringComparer.Ordinal);
        var sent = 0;
        if (baseline is not null)
        {
            foreach (var id in before.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                if (unreadable.Contains(CollectionOf(id))) continue;
                var now = before.GetValueOrDefault(id);
                if (Canonical(now) == baseline.GetValueOrDefault(id)) continue;
                if (now is not null) Stage(engine, now);
                else StageRemoval(engine, id);
                sent++;
            }
        }

        var pull = await engine.PullAsync(cancellationToken).ConfigureAwait(false);

        if (baseline is null)
        {
            // Primeira vez: junta sem perder nada.
            var account = Desired(engine);
            foreach (var (id, item) in before)
            {
                if (!account.TryGetValue(id, out var remote))
                {
                    Stage(engine, item);
                    sent++;
                }
                else if (item.Collection == Collections.Notes && Canonical(remote) != Canonical(item))
                {
                    engine.SetNotes(JoinNotes(remote.Data["text"]?.GetValue<string>() ?? string.Empty, item.Data["text"]?.GetValue<string>() ?? string.Empty, deviceName));
                    sent++;
                }
            }
        }

        // Aplicar aqui o que a conta tem de diferente, salvo o que o utilizador mudou durante esta ronda.
        var desired = Desired(engine);
        var second = Read(local);
        var during = Index(second.Items);
        unreadable.UnionWith(second.Unreadable);
        var changes = new List<LocalChange>();
        foreach (var id in desired.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal))
        {
            if (unreadable.Contains(CollectionOf(id))) continue;
            var want = desired.GetValueOrDefault(id);
            var have = before.GetValueOrDefault(id);
            if (Canonical(want) == Canonical(have)) continue;
            if (Canonical(during.GetValueOrDefault(id)) != Canonical(have)) continue;
            var (collection, key) = Split(id);
            changes.Add(new LocalChange(collection, key, want?.Data.DeepClone().AsObject()));
        }
        IReadOnlyCollection<LocalChange> applied = changes.Count == 0 ? [] : local.Apply(changes);

        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, item) in before) next[id] = Canonical(item)!;
        // O que não se leu fica na base como estava (a ronda seguinte, já com a leitura a funcionar, compara com ela).
        if (baseline is not null)
            foreach (var (id, value) in baseline)
                if (first.Unreadable.Contains(CollectionOf(id))) next[id] = value;
        foreach (var change in applied)
        {
            var id = Id(change.Collection, change.Key);
            if (change.Data is null) next.Remove(id);
            else next[id] = Canonical(change.Collection, change.Data)!;
        }

        // O envio (PushAsync) fica para quem chama, depois de guardar a base: um envio que falha não faz das alterações
        // recebidas e aplicadas aqui «alterações locais» na ronda seguinte.
        return (new SyncReport(sent, applied.Count, pull.Pending, pull.NeedsUpdate), next);
    }

    private static (string Collection, string Key) Split(string id)
    {
        var at = id.IndexOf('\n', StringComparison.Ordinal);
        return (id[..at], id[(at + 1)..]);
    }

    private static void Stage(SyncEngine engine, LocalItem item)
    {
        if (item.Collection == Collections.Notes)
        {
            var text = item.Data["text"]?.GetValue<string>() ?? string.Empty;
            if (text != engine.Notes) engine.SetNotes(text);
            return;
        }
        // Parte do registo que já existe: os campos que este Éteris não conhece seguem tal e qual.
        var data = engine.Get(item.Collection, item.Key) is { Deleted: false, Data: { } existing } ? existing.DeepClone().AsObject() : new JsonObject();
        foreach (var field in Fields[item.Collection])
        {
            if (item.Data[field] is { } value) data[field] = value.DeepClone();
            else data.Remove(field);
        }
        engine.Put(item.Collection, item.Key, data);
    }

    private static void StageRemoval(SyncEngine engine, string id)
    {
        var (collection, key) = Split(id);
        if (collection == Collections.Notes)
        {
            if (engine.Notes.Length > 0) engine.SetNotes(string.Empty);
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
