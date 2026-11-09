namespace Eteris.Sync.Model;

// Uma linha do tópico «O que passa entre PCs» (Definições › Sincronização): as coleções que liga e desliga juntas, se
// está ligada por omissão e se ligá-la pede a confirmação do Windows (Windows Hello). Os textos da linha são do
// anfitrião (Sync/SyncChoices no navegador); os identificadores e as omissões são partilhados com o Android.
public sealed record SyncGroup(string Id, IReadOnlyList<string> Collections, bool DefaultOn, bool AskToEnable);

public static class SyncGroups
{
    // Decisão D-1 do dono: Pagamentos e Documentos de identificação aparecem, desligados por omissão e com o Windows
    // Hello para ligar; as outras linhas ficam ligadas.
    public static readonly IReadOnlyList<SyncGroup> All =
    [
        new("bookmarks", [Collections.Bookmarks], DefaultOn: true, AskToEnable: false),
        new("spaces", [Collections.Spaces], DefaultOn: true, AskToEnable: false),
        new("settings", [Collections.Settings], DefaultOn: true, AskToEnable: false),
        new("shield", [Collections.Shield], DefaultOn: true, AskToEnable: false),
        new("notes", [Collections.Notes], DefaultOn: true, AskToEnable: false),
        new("passwords", [Collections.Logins, Collections.LoginsNever], DefaultOn: true, AskToEnable: false),
        new("payments", [Collections.Cards, Collections.Ibans], DefaultOn: false, AskToEnable: true),
        new("contacts", [Collections.Contacts], DefaultOn: true, AskToEnable: false),
        new("documents", [Collections.IdDocs], DefaultOn: false, AskToEnable: true),
        new("travel", [Collections.Travel], DefaultOn: true, AskToEnable: false),
    ];

    public static SyncGroup? Find(string? id) => All.FirstOrDefault(g => g.Id == id);

    // As coleções ligadas num aparelho que ainda não escolheu.
    public static IReadOnlySet<string> DefaultCollections { get; } =
        All.Where(g => g.DefaultOn).SelectMany(g => g.Collections).ToHashSet(StringComparer.Ordinal);

    // As que um aparelho de antes destas linhas já tinha juntado (as que existiam antes das coleções secretas).
    public static IReadOnlySet<string> LegacyCollections { get; } =
        new HashSet<string>([Collections.Bookmarks, Collections.Spaces, Collections.Settings, Collections.Shield, Collections.Notes], StringComparer.Ordinal);
}
