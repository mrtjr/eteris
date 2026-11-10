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

    // «account» (a escolha da conta) entra imediatamente antes de «Pagamentos»: ligada com a conta a sincronizar
    // pagamentos e documentos e os dois grupos ligados neste computador. As linhas por grupo ficam (desligar é por PC).
    public static IReadOnlyList<Row> Rows(IReadOnlySet<string> enabled, AccountChoice? account = null)
    {
        var rows = SyncGroups.All.Select(g => new Row(g.Id, Texts[g.Id].Title, Texts[g.Id].Text + (g.AskToEnable ? " · ligar pede o Windows Hello" : string.Empty),
            g.Collections.All(enabled.Contains), g.AskToEnable)).ToList();
        rows.Insert(rows.FindIndex(r => r.Id == SyncGroups.Payments), new Row(AccountRow, AccountLine,
            AccountText + (account is { On: true } chosen ? AccountSince(chosen.By, chosen.SinceMs) : string.Empty),
            account?.On == true && SyncGroups.AccountCollections.All(enabled.Contains), Ask: true));
        rows.Add(new Row("tabs", "Abas abertas", "Não passam: ficam em cada computador", null, false));
        rows.Add(new Row("passkeys", "Chaves de acesso", "Não passam: ficam no Windows Hello ou na chave de segurança", null, false));
        return rows;
    }

    public static string NotConfirmed(string what) => $"Não foi confirmado com o Windows: {what}.";

    // ---- escolha da conta: pagamentos e documentos (0.2.15, D4) -----------------------------------------------------

    // A linha da escolha da conta no tópico (o id vai na rota /sync-choose, como as outras).
    public const string AccountRow = "account";

    public const string AccountLine = "Também pagamentos e documentos de identificação";

    // O botão discreto na barra «Sincronizado.» do computador que criou a conta (abre a linha nas Definições).
    public const string AccountLink = "Também pagamentos e documentos…";

    // PROPOSTA de texto (o plano não o dá): a descrição por baixo da linha.
    public const string AccountText =
        "Cartões (sem o código de segurança), IBAN e documentos. Ligar pede o Windows Hello uma vez; os computadores que entrarem depois recebem-nos sem pedir de novo.";

    // A oferta única num computador que já estava ligado quando a escolha foi feita (barra e Definições).
    public const string OfferText = "Os seus outros computadores sincronizam agora pagamentos e documentos. Receber também neste?";
    public const string OfferYes = "Sim";
    public const string OfferNo = "Não";

    // O que o computador novo recebe, no pedido de aprovação (depois de «…quer entrar na sua conta e receber »).
    public static string Receives(bool accountSecrets)
        => accountSecrets ? "as suas palavras-passe, cartões e documentos" : PairingBarModel.DefaultReceives;

    // O que a barra «Sincronizado.» diz no computador que herdou a escolha (null = «Sincronizado.», como estava).
    public static string? ReceivedText(bool inherited) => inherited ? SyncAgent.InheritedText : null;

    // Os ganchos do estado «Sincronizado.» da barra do início de sessão (T4): no computador que herdou, o aviso
    // «Recebeu…» com «Abrir Definições» (onde se desliga); no que criou a conta nesta entrada, a ligação discreta
    // «Também pagamentos e documentos…» (nunca uma pergunta obrigatória). Senão, nada.
    public static (string? Text, SignInBarButton? Link) SyncedHooks(SyncStatus? status)
    {
        if (status is null) return (null, null);
        if (status is { InheritedSecrets: true, SecretsHere: true })
            return (ReceivedText(true), new SignInBarButton("Abrir Definições", SignInBarAction.OpenSettings));
        if (status is { Entry: SyncEntry.Created, AccountSecrets: false, SecretsHere: false })
            return (null, new SignInBarButton(AccountLink, SignInBarAction.AccountSecrets));
        return (null, null);
    }

    // A escolha da conta para a página: ligada, por quem e quando (By/SinceMs são informação, não prova).
    public sealed record AccountChoice(bool On, string? By, long SinceMs)
    {
        public static AccountChoice? Of(SyncAgent? agent)
            => agent is null ? null : new AccountChoice(agent.AccountSecrets, agent.AccountSecretsBy, agent.AccountSecretsSinceMs);
    }

    private const string NotOnHere = "A sincronização não está ligada neste computador.";

    // Onde e quando a escolha foi ligada (PROPOSTA de texto; informação, não prova: qualquer aparelho da conta a pode
    // escrever). «device» já vem limpo (SyncAgent.AccountSecretsBy). Data local, dd/MM/yyyy.
    public static string AccountSince(string? device, long sinceMs)
    {
        if (sinceMs <= 0) return string.Empty;
        var day = DateTimeOffset.FromUnixTimeMilliseconds(sinceMs).ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return device is { Length: > 0 } ? $" Ligado em «{device}» em {day}." : $" Ligado em {day}.";
    }

    // Um só Windows Hello para os dois grupos; grava a escolha na conta (os computadores que entrarem depois herdam-na).
    // null = feito; senão a mensagem para a página. Sem a sincronização ligada (ou ainda sem conta) aqui, recusa sem
    // pedir o Windows.
    public static async Task<string?> ChooseAccountAsync(SyncAgent agent, Func<ReauthPurpose, Task<bool>> confirm)
    {
        if (!agent.Enabled || !agent.HasKey) return NotOnHere;
        if (!await confirm(ReauthPurpose.SyncSecrets).ConfigureAwait(false)) return NotConfirmed($"«{AccountLine}» continua desligado");
        try { agent.SetAccountSecrets(true); }
        catch (SyncUserException e) { return e.Message; }
        agent.SetCollections(SyncGroups.AccountCollections, true);
        return null;
    }

    // A resposta à oferta única («receber também neste?»). «Sim» pede o Windows Hello: recusado, a oferta fica por
    // responder e nada muda.
    public static async Task<string?> AnswerOfferAsync(SyncAgent agent, bool accept, Func<ReauthPurpose, Task<bool>> confirm)
    {
        if (accept && !await confirm(ReauthPurpose.SyncSecrets).ConfigureAwait(false)) return NotConfirmed($"«{AccountLine}» continua desligado");
        agent.AnswerSecretsOffer(accept);
        return null;
    }

    // Liga ou desliga uma linha neste computador. Devolve a mensagem de erro (null = feito). A linha da conta liga com a
    // escolha da conta; desligá-la só desliga os dois grupos aqui (a conta fica como está: desligar não apaga nada).
    public static async Task<string?> ChooseAsync(SyncAgent agent, string groupId, bool on, Func<ReauthPurpose, Task<bool>> confirm)
    {
        if (groupId == AccountRow)
        {
            if (on) return await ChooseAccountAsync(agent, confirm).ConfigureAwait(false);
            agent.SetCollections(SyncGroups.AccountCollections, false);
            return null;
        }
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

    public static object Data(IReadOnlySet<string> enabled, AccountChoice? account = null) => new
    {
        intro = Intro,
        rows = Rows(enabled, account).Select(r => new { id = r.Id, title = r.Title, text = r.Text, on = r.On, ask = r.Ask }),
    };
}
