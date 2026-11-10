using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// As saídas da conta (0.2.15, T12), como lógica pura: sem estado nem estáticos mutáveis (o SyncService é a cola).
//  - «Sair deste computador»: só local (GoogleSync.SignOut); nada é revogado na Google.
//  - «Retirar o acesso do Éteris à conta Google»: revoga. Só se diz «retirado» quando a Google o confirma (Revoked).
//  - «Perdi um computador / foi roubado» e «Começar do zero…».
// Os textos marcados PROPOSTA não vêm do plano: o dono aprova-os (lista no relatório da T12).
internal static class SyncExit
{
    // ---- «Sair» (à letra do plano; SignOutKeeps é PROPOSTA) -----------------------------------------------------------

    public const string SignOutHelp = "Sai só deste computador. A Google continua a listar o Éteris na sua conta até usar «Retirar o acesso».";
    public const string SignOutKeeps = "A chave da sincronização e os dados ficam neste computador, a não ser que marque «apagar também a chave».";

    // ---- «Retirar o acesso» (título e texto do plano; o resto é PROPOSTA) ----------------------------------------------

    public const string RevokeTitle = "Retirar o acesso do Éteris à conta Google";
    public const string RevokeText = "Revoga a autorização na Google e desliga todos os seus computadores. Também em myaccount.google.com/permissions.";
    public const string RevokeButton = "Retirar o acesso…";
    public const string RevokeQuestion = "Todos os seus computadores saem da conta e têm de entrar outra vez. Os dados no Drive e a chave deste computador ficam.";
    public const string RevokeConfirm = "Sim, retirar o acesso";
    public const string RevokeDone = "O acesso do Éteris à conta Google foi retirado: todos os computadores ficaram desligados. Para voltar a sincronizar, entre outra vez.";
    public const string RevokeFailed = "Não foi possível falar com a Google: o acesso não foi retirado. Tente outra vez, ou retire-o em myaccount.google.com/permissions.";
    public const string RevokeNoSession = "Não há sessão iniciada na Google neste computador: o acesso não foi retirado. Entre primeiro, ou retire-o em myaccount.google.com/permissions.";
    public const string RevokeUnconfirmed = "A sessão deste computador na Google já não era válida, por isso não foi possível confirmar que o acesso foi retirado. Entre outra vez e repita «Retirar o acesso», ou retire-o em myaccount.google.com/permissions.";

    // ---- «Perdi um computador» (LostTitle e LostDone do plano; AdvicePasswords do produto; o resto é PROPOSTA) ---------

    public const string LostTitle = "Perdi um computador / foi roubado";
    public const string LostStep1 = "1. O aparelho sai da conta e a conta muda de chave: daqui em diante, esse computador não recebe nem consegue abrir nada novo. O que ele já tinha continua lá.";
    public const string LostStep2 = "2. O acesso do Éteris à conta Google é retirado em todos os seus computadores: sem isto, o computador perdido ainda podia apagar ou estragar os ficheiros no Drive.";
    public const string LostStep3 = "3. Depois, entra outra vez neste e nos seus outros computadores e guarda o kit novo: o antigo deixa de valer.";
    public const string LostConfirm = "Confirmar: remover e retirar o acesso";
    public const string LostConfirmFolder = "Confirmar: remover";
    public const string LostDone = "Entre outra vez nos seus outros computadores (3 ações em cada) e guarde o kit novo: o antigo deixou de valer.";
    public const string LostHere = "Este computador também saiu da conta Google: entre outra vez aqui para continuar a sincronizar e para ver o kit novo.";
    public const string LostKeeps = "O que já estava no computador removido continua lá; daqui em diante ele não recebe nada novo.";
    public const string AdvicePasswords = "O aparelho removido já tinha as suas palavras-passe: mude as dos sites importantes.";
    public const string AdviceCards = "Também já tinha cartões, IBAN ou documentos de identificação: trate-os como vistos.";
    public const string LostGoogle = "Se a sua conta Google estava aberta num navegador desse computador, termine essa sessão na conta Google e mude a palavra-passe da Google: quem o tiver podia voltar a entrar no Éteris.";
    public const string LostNotRevoked = "O aparelho foi removido, mas não foi possível retirar o acesso na Google. Use «Retirar o acesso do Éteris à conta Google», nesta página, ou myaccount.google.com/permissions.";
    public const string LostUnconfirmed = "O aparelho foi removido, mas não foi possível confirmar que o acesso foi retirado na Google. Entre outra vez e use «Retirar o acesso do Éteris à conta Google», ou myaccount.google.com/permissions.";
    public const string LostHalfway = "A remoção ficou a meio e acaba sozinha na próxima sincronização. O acesso na Google ainda não foi retirado: quando a remoção acabar, use «Retirar o acesso do Éteris à conta Google».";
    public const string LostFolderDone = "O aparelho foi removido e a conta mudou de chave. Na pasta de teste não há acesso da Google para retirar.";

    // ---- «Começar do zero…» (StartOverText do plano; o resto é PROPOSTA) ---------------------------------------------

    public const string StartOverText = "A conta antiga na nuvem é apagada. O que só estava nela perde-se. O que está neste computador fica e passa para a conta nova.";
    public const string StartOverWord = "começar do zero";
    public const string StartOverWrongWord = "Para começar do zero, escreva «começar do zero» na caixa.";
    public const string StartOverWrongPhase = "Este computador já está numa conta de sincronização: «Começar do zero» só existe antes de entrar.";
    public const string StartOverDone = "Conta nova criada a partir deste computador. A antiga foi apagada da nuvem: os seus outros computadores vão dizer que a conta foi apagada ou substituída. Guarde o kit novo.";
    public const string StartOverNotDone = "A conta antiga foi apagada, mas entretanto apareceu outra neste Google Drive. Escolha como entrar.";

    // ---- «Retirar o acesso» --------------------------------------------------------------------------------------------

    // Revoga. Done = a Google CONFIRMOU (só Revoked). stopHere corre sempre que a sessão daqui ficou apagada (Revoked e
    // AlreadyInvalid), no máximo uma vez; nunca com Failed nem NotSignedIn. Um cancelamento (o limite de tempo) é Failed.
    public static async Task<(bool Done, string Message)> RevokeAsync(
        Func<CancellationToken, Task<RevokeOutcome>> revoke, Action stopHere, CancellationToken cancellationToken)
    {
        RevokeOutcome outcome;
        try { outcome = await revoke(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { outcome = RevokeOutcome.Failed; }
        switch (outcome)
        {
            case RevokeOutcome.Revoked:
                stopHere();
                return (true, RevokeDone);
            case RevokeOutcome.AlreadyInvalid:
                // Os tokens daqui já saíram: sem isto a sincronização ficava ligada sem sessão. Mas nada diz que a
                // autorização da conta acabou (R6).
                stopHere();
                return (false, RevokeUnconfirmed);
            case RevokeOutcome.NotSignedIn:
                return (false, RevokeNoSession);
            default:
                return (false, RevokeFailed);
        }
    }

    // ---- «Perdi um computador / foi roubado» ---------------------------------------------------------------------------

    // A porta do Windows: sempre, haja ou não segredos na conta (S27). true = pode continuar.
    public static Task<bool> MayLoseDeviceAsync(Func<ReauthPurpose, Task<bool>> confirm) => confirm(ReauthPurpose.LostDevice);

    // Revoked = a Google confirmou a revogação (só RevokeOutcome.Revoked).
    public sealed record LostResult(bool Removed, bool Revoked, string Message);

    // 1) remove o aparelho (cadeia nova: o perdido não abre nada do que vier a seguir); 2) só se a troca de chave acabou
    // por inteiro, revoga (revogar antes deixava os outros sem forma de receber a chave nova); 3) a mensagem.
    // revoke null = pasta de teste (sem Google): só o passo 1 e a mensagem.
    public static async Task<LostResult> LostDeviceAsync(SyncAgent agent, string device, IReadOnlyCollection<string>? shown,
        Func<CancellationToken, Task<RevokeOutcome>>? revoke, Action stopHere, CancellationToken cancellationToken)
    {
        try { await agent.RemoveDeviceAsync(device, shown, cancellationToken).ConfigureAwait(false); }
        catch (SyncUserException e)
        {
            return new LostResult(false, false, agent.RotationPending ? e.Message + " " + LostHalfway : e.Message);
        }
        if (agent.RotationPending) return new LostResult(true, false, LostHalfway);
        // Lido antes de parar a sincronização aqui (parar esquece-o).
        var secrets = agent.SecretCollectionsInAccount;
        if (revoke is null) return new LostResult(true, false, Lines(LostFolderDone, LostKeeps, LostAdvice(secrets)));
        RevokeOutcome outcome;
        try { outcome = await revoke(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { outcome = RevokeOutcome.Failed; }
        switch (outcome)
        {
            case RevokeOutcome.Revoked:
                stopHere();
                return new LostResult(true, true, LostMessage(secrets));
            case RevokeOutcome.AlreadyInvalid:
                // A sessão daqui saiu, mas a autorização pode continuar viva (R6): nada de LostDone nem LostHere.
                stopHere();
                return new LostResult(true, false, Lines(LostUnconfirmed, LostKeeps, LostAdvice(secrets)));
            default:
                return new LostResult(true, false, LostNotRevoked);
        }
    }

    // O conselho por tipo de segredo que a conta tinha (cadeia vazia sem nenhum).
    public static string LostAdvice(IReadOnlySet<string> secretCollections)
        => Lines(secretCollections.Contains(Collections.Logins) ? AdvicePasswords : "",
            secretCollections.Contains(Collections.Cards) || secretCollections.Contains(Collections.Ibans) || secretCollections.Contains(Collections.IdDocs)
                ? AdviceCards : "");

    // A mensagem final com a revogação feita, uma frase por linha.
    public static string LostMessage(IReadOnlySet<string> secretCollections)
        => Lines(LostDone, LostHere, LostKeeps, LostAdvice(secretCollections), LostGoogle);

    private static string Lines(params string[] lines) => string.Join("\n", lines.Where(l => l.Length > 0));

    // ---- «Começar do zero…» -------------------------------------------------------------------------------------------

    // A confirmação escrita: «começar do zero», sem contar espaços a mais, maiúsculas nem acentos.
    public static bool StartOverWordMatches(string? typed) => typed is not null && Plain(typed) == Plain(StartOverWord);

    private static string Plain(string text)
    {
        var spaced = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var decomposed = spaced.Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    // A porta do Windows de «Começar do zero…»: sempre. true = pode continuar.
    public static Task<bool> MayStartOverAsync(Func<ReauthPurpose, Task<bool>> confirm) => confirm(ReauthPurpose.StartOver);

    // Só num computador que ainda não entrou na conta (Choose, Pairing): apaga a conta antiga da nuvem (o «Apagar» que já
    // existe) e cria uma nova a partir deste computador (a ronda do gesto, com o Drive vazio, cria a conta; o que está
    // aqui sobe nela).
    public static async Task<(bool Done, string Message)> StartOverAsync(SyncAgent agent, CancellationToken cancellationToken)
    {
        if (agent.Status.Phase is not (SyncPhase.Choose or SyncPhase.Pairing)) return (false, StartOverWrongPhase);
        try
        {
            agent.CancelPairing();
            await agent.DeleteCloudDataAsync(cancellationToken).ConfigureAwait(false);
            await agent.StartAsync(switchAccount: true, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncUserException e) { return (false, e.Message); }
        return agent.Status.Phase == SyncPhase.On ? (true, StartOverDone) : (false, agent.Status.Error ?? StartOverNotDone);
    }
}
