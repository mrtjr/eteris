using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// O tópico «O que passa entre PCs» (Definições › Sincronização; desenho do gestor de palavras-passe, secção 4.6): uma
// linha por grupo de coleções (Eteris.Sync.Model.SyncGroups), com o interruptor deste computador, e as linhas só de
// informação (abas abertas, chaves de acesso). Ligar Pagamentos ou Documentos de identificação pede o Windows Hello
// (sempre, sem a janela de graça: decisão D-1). Aprovar um aparelho novo com segredos em jogo e exportar o perfil com
// segredos também pedem.
internal static class SyncChoices
{
    // On null = linha só de informação (sem interruptor).
    public sealed record Row(string Id, string Title, string Text, bool? On, bool Ask);

    public const string Intro =
        "Tudo é cifrado neste computador antes de sair. A Google só guarda ficheiros ilegíveis, os tamanhos e as horas, e a chave nunca lá vai. As palavras-passe, os pagamentos e os documentos levam uma segunda cifra própria. Cada computador escolhe o que recebe: desligar uma linha não apaga nada. Quem perder o kit de recuperação e todos os computadores perde estes dados: não há «esqueci-me do kit».";

    private static readonly IReadOnlyDictionary<string, (string Title, string Text)> Texts = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["bookmarks"] = ("Favoritos", "Os favoritos e as pastas"),
        ["spaces"] = ("Espaços", "Nome, cor e emoji; as abas de cada Espaço ficam em cada computador"),
        ["settings"] = ("Definições", "Só as que fazem sentido noutro computador"),
        ["shield"] = ("Decisões do Escudo", "Sites com o Escudo desligado ou estrito, elementos escondidos, bloqueios por site"),
        ["notes"] = ("Notas", "O texto das notas"),
        ["passwords"] = ("Palavras-passe", "Palavras-passe, notas e sites onde nunca guardar"),
        ["payments"] = ("Pagamentos", "Cartões (sem o código de segurança) e IBAN"),
        ["contacts"] = ("Informações de contacto", "Nomes, moradas, e-mails e telefones"),
        ["documents"] = ("Documentos de identificação", "Passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF"),
        ["travel"] = ("Viagens", "Veículos e programas de fidelidade"),
    };

    public static IReadOnlyList<Row> Rows(IReadOnlySet<string> enabled)
    {
        var rows = SyncGroups.All.Select(g => new Row(g.Id, Texts[g.Id].Title, Texts[g.Id].Text + (g.AskToEnable ? " · ligar pede o Windows Hello" : string.Empty),
            g.Collections.All(enabled.Contains), g.AskToEnable)).ToList();
        rows.Add(new Row("tabs", "Abas abertas", "Não passam: ficam em cada computador", null, false));
        rows.Add(new Row("passkeys", "Chaves de acesso", "Não passam: ficam no Windows Hello ou na chave de segurança", null, false));
        return rows;
    }

    public static string NotConfirmed(string what) => $"Não foi confirmado com o Windows: {what}.";

    // Liga ou desliga uma linha neste computador. Devolve a mensagem de erro (null = feito).
    public static async Task<string?> ChooseAsync(SyncAgent agent, string groupId, bool on, Func<ReauthPurpose, Task<bool>> confirm)
    {
        if (SyncGroups.Find(groupId) is not { } group) return "Esta linha não existe.";
        if (on && group.AskToEnable && !await confirm(ReauthPurpose.SyncSecrets).ConfigureAwait(false))
            return NotConfirmed($"«{Texts[group.Id].Title}» continua desligado");
        agent.SetCollections(group.Collections, on);
        return null;
    }

    // Aprovar um aparelho novo: com segredos em jogo (coleções do cofre ligadas aqui ou registos secretos na conta), ele
    // vai receber as palavras-passe, os pagamentos… e por isso pede o Windows Hello. true = pode aprovar.
    public static async Task<bool> MayApproveAsync(SyncAgent agent, Func<ReauthPurpose, Task<bool>> confirm)
        => !agent.HasSecrets || await confirm(ReauthPurpose.ApproveDevice).ConfigureAwait(false);

    // Um kit de recuperação novo (pedido ou depois de remover um aparelho) também abre o cofre: com segredos em jogo,
    // quem o vê pode juntar outro computador e receber tudo. Pede o Windows Hello como aprovar um aparelho (sempre).
    public static Task<bool> MayRenewKeyAsync(SyncAgent agent, Func<ReauthPurpose, Task<bool>> confirm) => MayApproveAsync(agent, confirm);

    // «Ver o kit» de recuperação: pede SEMPRE a confirmação do Windows (com ou sem segredos ligados: o kit abre a conta
    // inteira). true = pode mostrar.
    public static Task<bool> MayRevealKitAsync(Func<ReauthPurpose, Task<bool>> confirm) => confirm(ReauthPurpose.ShowRecoveryKit);

    // «Foi eu — juntar este computador» (alerta de conta substituída): com coleções secretas ligadas NESTE computador,
    // juntá-lo à conta que está agora no Drive manda para lá as palavras-passe. O Windows confirma primeiro (sempre).
    public static async Task<bool> MayJoinReplacedAsync(SyncAgent agent, Func<ReauthPurpose, Task<bool>> confirm)
        => !agent.EnabledCollections.Any(Collections.IsSecret) || await confirm(ReauthPurpose.JoinReplacedAccount).ConfigureAwait(false);

    public const string ApproveWarning =
        "Esse computador vai receber também as suas palavras-passe, informações de contacto e viagens (e os pagamentos e documentos, se estiverem ligados): aprovar pede o Windows Hello.";

    // Os itens do cofre para um perfil exportado: sem eles por omissão; com eles só depois do Windows Hello (sempre).
    // (Ok = false: não foi confirmado e nada se exporta.)
    public static async Task<(bool Ok, IReadOnlyList<ArchiveSecret>? Secrets)> SecretsForExportAsync(bool withSecrets, Func<ReauthPurpose, Task<bool>> confirm,
        Func<IReadOnlyList<ArchiveSecret>> collect)
    {
        if (!withSecrets) return (true, null);
        if (!await confirm(ReauthPurpose.ExportProfileSecrets).ConfigureAwait(false)) return (false, null);
        return (true, collect());
    }

    public static object Data(IReadOnlySet<string> enabled) => new
    {
        intro = Intro,
        rows = Rows(enabled).Select(r => new { id = r.Id, title = r.Title, text = r.Text, on = r.On, ask = r.Ask }),
    };
}
