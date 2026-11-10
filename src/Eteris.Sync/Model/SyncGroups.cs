namespace Eteris.Sync.Model;

// Uma linha do tópico «O que passa entre PCs» (Definições › Sincronização): as coleções que liga e desliga juntas, se
// está ligada por omissão e se ligá-la pede a confirmação do Windows (Windows Hello). Os textos da linha são do
// anfitrião (Sync/SyncChoices no navegador); os identificadores e as omissões são partilhados com o Android.
public sealed record SyncGroup(string Id, IReadOnlyList<string> Collections, bool DefaultOn, bool AskToEnable);

public static class SyncGroups
{
    public const string Payments = "payments";
    public const string Documents = "documents";

    // Decisão D-1 do dono: Pagamentos e Documentos de identificação aparecem, desligados por omissão e com o Windows
    // Hello para ligar; as outras linhas ficam ligadas. Desde a 0.2.15 (D4) há também a escolha da conta, que liga os
    // dois de uma vez e que os computadores que entram depois herdam (SyncAgent); as omissões daqui não mudam.
    public static readonly IReadOnlyList<SyncGroup> All =
    [
        new("bookmarks", [Collections.Bookmarks], DefaultOn: true, AskToEnable: false),
        new("spaces", [Collections.Spaces], DefaultOn: true, AskToEnable: false),
        new("settings", [Collections.Settings], DefaultOn: true, AskToEnable: false),
        new("shield", [Collections.Shield], DefaultOn: true, AskToEnable: false),
        new("notes", [Collections.Notes], DefaultOn: true, AskToEnable: false),
        new("passwords", [Collections.Logins, Collections.LoginsNever], DefaultOn: true, AskToEnable: false),
        new(Payments, [Collections.Cards, Collections.Ibans], DefaultOn: false, AskToEnable: true),
        new("contacts", [Collections.Contacts], DefaultOn: true, AskToEnable: false),
        new(Documents, [Collections.IdDocs], DefaultOn: false, AskToEnable: true),
        new("travel", [Collections.Travel], DefaultOn: true, AskToEnable: false),
    ];

    // Os grupos que a escolha da conta liga de uma vez (0.2.15, D4).
    public static readonly IReadOnlyList<string> AccountGroups = [Payments, Documents];

    // As coleções desses grupos: cards, ibans, idDocs.
    public static IReadOnlySet<string> AccountCollections { get; } =
        All.Where(g => AccountGroups.Contains(g.Id)).SelectMany(g => g.Collections).ToHashSet(StringComparer.Ordinal);

    public static SyncGroup? Find(string? id) => All.FirstOrDefault(g => g.Id == id);

    // Os ids dos grupos ligados por inteiro num aparelho com estas coleções (o campo «groups» do estado do aparelho).
    public static IReadOnlyList<string> GroupsOn(IReadOnlySet<string> enabled)
        => All.Where(g => g.Collections.All(enabled.Contains)).Select(g => g.Id).ToList();

    // As coleções ligadas num aparelho que ainda não escolheu.
    public static IReadOnlySet<string> DefaultCollections { get; } =
        All.Where(g => g.DefaultOn).SelectMany(g => g.Collections).ToHashSet(StringComparer.Ordinal);

    // As que um aparelho de antes destas linhas já tinha juntado (as que existiam antes das coleções secretas).
    public static IReadOnlySet<string> LegacyCollections { get; } =
        new HashSet<string>([Collections.Bookmarks, Collections.Spaces, Collections.Settings, Collections.Shield, Collections.Notes], StringComparer.Ordinal);
}
