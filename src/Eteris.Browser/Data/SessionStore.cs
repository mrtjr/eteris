using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Data;

// O nível de cima é uma janela (a 1.ª, a dona: a sua posição fica em window.json). As outras janelas normais vão em
// Windows, pela ordem em que abriram, cada uma com a sua sessão completa e a sua posição. Um ficheiro sem Windows
// (o formato antigo, ou uma só janela) lê-se como uma janela, e um Éteris antigo ignora o campo e abre a 1.ª.
public sealed record Session(IReadOnlyList<string> Urls, int ActiveIndex, IReadOnlyList<string>? Titles = null,
    IReadOnlyList<bool>? Pinned = null, IReadOnlyList<int>? GroupIds = null, IReadOnlyList<SessionGroup>? Groups = null,
    SessionSplit? Split = null, IReadOnlyList<SessionTabMeta>? Metas = null, IReadOnlyList<SessionSpace>? Spaces = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int SpaceIndex = 0,
    IReadOnlyList<SessionWindow>? Windows = null);

// Uma janela normal além da 1.ª: as suas abas (com os seus Espaços; um Windows lá dentro ignora-se) e onde estava.
public sealed record SessionWindow(Session Tabs, Infrastructure.WindowPlacement? Placement = null);

// Espaço (Tabs/Workspace.cs) gravado na sessão: nome, cor e ícone; as abas dos Espaços que não estão à vista vão
// numa sessão própria (Tabs), e o que está à vista (SpaceIndex) usa as abas do nível de cima, como sempre — assim
// uma sessão sem Espaços lê-se como antes e um Éteris sem esta funcionalidade ignora o campo.
// Left = desde quando está posto de lado (Unix ms), para o arquivo automático não contar esse tempo.
public sealed record SessionSpace(string Name, int ColorIndex, string? Icon = null, Session? Tabs = null, long? Left = null);

// O que o utilizador deu à aba (Tabs/TabIdentity.cs): nome próprio, emoji e cor do ícone; tudo opcional.
// Seen = última visita (Unix ms), para o arquivo automático contar o tempo real e não desde o último arranque.
// Home = a casa de uma aba fixada (Tabs/PinnedHome.cs), só nas fixadas; um Éteris antigo ignora o campo.
public sealed record SessionTabMeta(string? Name = null, string? Emoji = null, int? Color = null, string? Note = null, bool Locked = false,
    long? Seen = null, string? Home = null)
{
    public static readonly SessionTabMeta Empty = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Name is null && Emoji is null && Color is null && Note is null && !Locked && Seen is null && Home is null;
}

// Ecrã dividido gravado na sessão: índices das duas abas (na lista gravada) e a orientação («side» / «stacked»).
public sealed record SessionSplit(int Left, int Right, string Orientation)
{
    public bool IsValid(int tabCount) => Left != Right && Left >= 0 && Right >= 0 && Left < tabCount && Right < tabCount;
}

// Grupo de abas gravado na sessão; cada aba guarda o índice do seu grupo (-1 = nenhum).
public sealed record SessionGroup(string Name, int ColorIndex, bool Collapsed);

// Abas abertas, gravadas continuamente para sobreviverem a um fecho normal ou a um crash.
// Sem limite de abas: as que não estão à vista são repostas a dormir (sem motor), e cortar a lista perdia em silêncio
// as abas a mais — da faixa e dos Espaços na reserva — no arranque seguinte.
public sealed class SessionStore(string path)
{
    // Campos a null (divisão, identidades) ficam fora do ficheiro: lê-se da mesma e o JSON fica legível.
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public void Save(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles = null, IEnumerable<bool>? pinned = null,
        IEnumerable<int>? groupIds = null, IReadOnlyList<SessionGroup>? groups = null, SessionSplit? split = null,
        IEnumerable<SessionTabMeta?>? metas = null, IReadOnlyList<SessionSpace>? spaces = null, int spaceIndex = 0)
        => Write(Compose(urls, activeIndex, titles, pinned, groupIds, groups, split, metas, spaces, spaceIndex));

    // Todas as janelas normais: a 1.ª no nível de cima, as outras em Windows (com uma só, o ficheiro fica igual ao
    // de sempre). Lista vazia não escreve (nunca se grava uma sessão sem janelas). Nunca lança, como TrySave.
    public bool TrySaveWindows(IReadOnlyList<SessionWindow> windows)
    {
        if (windows.Count == 0) return true;
        var others = windows.Count > 1 ? windows.Skip(1).Select(w => w with { Tabs = w.Tabs with { Windows = null } }).ToList() : null;
        try
        {
            Write(windows[0].Tabs with { Windows = others });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTemporary();
            return false;
        }
    }

    // A sessão filtrada de uma janela (só o que se pode repor, com os Espaços), sem escrever.
    public static Session Compose(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles = null, IEnumerable<bool>? pinned = null,
        IEnumerable<int>? groupIds = null, IReadOnlyList<SessionGroup>? groups = null, SessionSplit? split = null,
        IEnumerable<SessionTabMeta?>? metas = null, IReadOnlyList<SessionSpace>? spaces = null, int spaceIndex = 0)
    {
        var session = Build(urls, activeIndex, titles, pinned, groupIds, groups, split, metas);
        if (spaces is { Count: > 0 })
        {
            // Cada Espaço na reserva leva a sua sessão filtrada pelas mesmas regras; o que está à vista vai sem abas.
            var kept = spaces.Select((space, i) => space with
            {
                Tabs = i == spaceIndex || space.Tabs is null ? null
                    : Build(space.Tabs.Urls, space.Tabs.ActiveIndex, space.Tabs.Titles, space.Tabs.Pinned, space.Tabs.GroupIds, space.Tabs.Groups,
                        space.Tabs.Split, space.Tabs.Metas),
            }).ToList();
            session = session with { Spaces = kept, SpaceIndex = Math.Clamp(spaceIndex, 0, kept.Count - 1) };
        }
        return session;
    }

    // Como a última leitura correu (nenhuma escrita a muda): a janela avisa quando o ficheiro estava danificado.
    public Infrastructure.AtomicRead LastLoad { get; private set; } = Infrastructure.AtomicRead.Missing;

    private readonly Lock _writing = new();
    private bool _setAside;

    // Descarregado para o disco e com a versão anterior em session.json.bak (Infrastructure/AtomicFile.cs). O que não se
    // leu nunca se grava por cima: depois de um Unreadable, a 1.ª escrita põe o ficheiro antigo em .corrupt.
    private void Write(Session session)
    {
        var json = JsonSerializer.Serialize(session, Options);
        lock (_writing)
        {
            if (LastLoad == Infrastructure.AtomicRead.Unreadable && !_setAside)
            {
                Infrastructure.AtomicFile.SetAside(path);
                _setAside = true;
            }
            Infrastructure.AtomicFile.WriteText(path, json, backup: true);
        }
    }

    // Como Save, mas nunca lança: um ficheiro só de leitura, preso por outro programa ou um .tmp que não se deixa
    // escrever dão false (a sessão fica por gravar e quem chama avisa) em vez de fechar o Éteris.
    public bool TrySave(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles = null, IEnumerable<bool>? pinned = null,
        IEnumerable<int>? groupIds = null, IReadOnlyList<SessionGroup>? groups = null, SessionSplit? split = null,
        IEnumerable<SessionTabMeta?>? metas = null, IReadOnlyList<SessionSpace>? spaces = null, int spaceIndex = 0)
    {
        try
        {
            Save(urls, activeIndex, titles, pinned, groupIds, groups, split, metas, spaces, spaceIndex);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTemporary();
            return false;
        }
    }

    // O .tmp de uma escrita que falhou não fica no perfil (se nem isso se deixar apagar, fica para a próxima escrita).
    private void DeleteTemporary()
    {
        try { File.Delete(path + ".tmp"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Só o que se pode repor (http, https, eteris://), todas as abas, com os campos por aba alinhados.
    private static Session Build(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles, IEnumerable<bool>? pinned,
        IEnumerable<int>? groupIds, IReadOnlyList<SessionGroup>? groups, SessionSplit? split, IEnumerable<SessionTabMeta?>? metas)
    {
        var allGroupIds = groupIds?.ToList() ?? [];
        var allMetas = metas?.ToList() ?? [];
        var all = urls.ToList();
        var allTitles = titles?.ToList() ?? [];
        var allPinned = pinned?.ToList() ?? [];
        var keptIndices = Enumerable.Range(0, all.Count).Where(i => IsRestorable(all[i])).ToList();
        var kept = keptIndices.Select(i => all[i]).ToList();
        var keptTitles = keptIndices.Select(i => i < allTitles.Count ? allTitles[i] : string.Empty).ToList();
        var keptPinned = keptIndices.Select(i => i < allPinned.Count && allPinned[i]).ToList();
        var active = Math.Max(0, keptIndices.IndexOf(activeIndex));
        var keptGroupIds = keptIndices.Select(i => i < allGroupIds.Count ? allGroupIds[i] : -1).ToList();
        var keptSplit = Remap(split, keptIndices);
        var keptMetas = keptIndices.Select(i => i < allMetas.Count ? allMetas[i] ?? SessionTabMeta.Empty : SessionTabMeta.Empty).ToList();
        // Sem nenhuma aba com nome, emoji ou cor, o campo nem vai para o ficheiro.
        var metasOrNull = keptMetas.All(m => m.IsEmpty) ? null : keptMetas;
        return new Session(kept, active, keptTitles, keptPinned, keptGroupIds, groups ?? [], keptSplit, metasOrNull);
    }

    public Session? Load()
    {
        try
        {
            var session = Infrastructure.AtomicFile.ReadText(path, json => Read(json) is { Urls: not null } read ? read : null, out var outcome);
            LastLoad = outcome;
            if (session is null) return null;
            // A janela do nível de cima e as de Windows (saltam-se as entradas sem sessão), cada uma filtrada; ficam
            // só as que têm o que repor, e a 1.ª que sobrar passa a ser a de cima.
            var windows = new List<SessionWindow> { new(session) };
            if (session.Windows is { } others)
                windows.AddRange(others.Where(w => w?.Tabs?.Urls is not null));
            var kept = windows.Select(w => (Window: w, Tabs: FilterWindow(w.Tabs))).Where(x => x.Tabs is not null)
                .Select(x => x.Window with { Tabs = x.Tabs! }).ToList();
            if (kept.Count == 0) return null;
            return kept[0].Tabs with { Windows = kept.Count > 1 ? kept.Skip(1).ToList() : null };
        }
        // Um ficheiro de cache nunca impede o arranque: valores fora do intervalo, sem permissão de leitura, etc.
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            return null;
        }

    }

    // Ajuste e12: lida com o código gerado (Data/StartupJson.cs); sem ele, por reflexão, como sempre. O mesmo resultado.
    public static Session? Read(string json)
        => Infrastructure.PerfTuning.IsOn(Infrastructure.PerfTuning.ReadyNewTab)
            ? JsonSerializer.Deserialize(json, StartupJsonContext.Default.Session)
            : JsonSerializer.Deserialize<Session>(json);

    // Uma janela lida do disco, filtrada com os seus Espaços (sem Windows interior); null se só tiver páginas
    // internas em todos os Espaços: não vale a pena repor (a janela arranca como nova).
    private static Session? FilterWindow(Session session)
    {
        var spaces = session.Spaces?.Where(s => s is { Name.Length: > 0 })
            .Select(s => s with { Tabs = s.Tabs?.Urls is null ? null : Filter(s.Tabs) }).ToList();
        if (spaces is { Count: 0 }) spaces = null;
        var main = Filter(session);
        if (IsOnlyInternal(main) && (spaces is null || spaces.All(s => s.Tabs is null || IsOnlyInternal(s.Tabs)))) return null;
        return main with { Spaces = spaces, SpaceIndex = spaces is null ? 0 : Math.Clamp(session.SpaceIndex, 0, spaces.Count - 1) };
    }

    // Os endereços de uma janela gravada: a faixa e os Espaços na reserva.
    public static IEnumerable<string> Addresses(Session session)
        => session.Urls.Concat(session.Spaces?.SelectMany(s => s.Tabs?.Urls ?? []) ?? []);

    private static bool IsOnlyInternal(Session session)
        => session.Urls.All(u => u.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase));

    // Uma sessão lida do disco, só com o que se pode repor e com os campos por aba alinhados.
    // Entradas null (ficheiro editado à mão ou estragado) não lançam: endereço null não se repõe, título null fica
    // vazio e um grupo null fica sem nome (os índices dos grupos mantêm-se).
    private static Session Filter(Session session)
    {
        var indices = Enumerable.Range(0, session.Urls.Count).Where(i => IsRestorable(session.Urls[i])).ToList();
        var urls = indices.Select(i => session.Urls[i]).ToList();
        var titles = indices.Select(i => session.Titles is { } t && i < t.Count ? t[i] ?? string.Empty : string.Empty).ToList();
        var pinned = indices.Select(i => session.Pinned is { } p && i < p.Count && p[i]).ToList();
        var groupCount = session.Groups?.Count ?? 0;
        var groupIds = indices.Select(i => session.GroupIds is { } g && i < g.Count && g[i] < groupCount ? g[i] : -1).ToList();
        var metas = indices.Select(i => session.Metas is { } m && i < m.Count ? m[i] ?? SessionTabMeta.Empty : SessionTabMeta.Empty).ToList();
        var groups = session.Groups?.Select(g => g is { Name: not null } ? g : new SessionGroup(g?.Name ?? "", g?.ColorIndex ?? 0, g?.Collapsed ?? false))
            .ToList() ?? [];
        return new Session(urls, Math.Clamp(session.ActiveIndex, 0, Math.Max(0, urls.Count - 1)), titles, pinned, groupIds, groups,
            Remap(session.Split, indices), metas);
    }

    // A divisão só sobrevive se as duas abas sobreviverem à filtragem; os índices passam a ser os da lista filtrada.
    private static SessionSplit? Remap(SessionSplit? split, List<int> keptIndices)
    {
        if (split is null) return null;
        var left = keptIndices.IndexOf(split.Left);
        var right = keptIndices.IndexOf(split.Right);
        var remapped = new SessionSplit(left, right, split.Orientation ?? string.Empty);
        return remapped.IsValid(keptIndices.Count) ? remapped : null;
    }

    private static bool IsRestorable(string? url)
        => url is not null
           && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase));
}
