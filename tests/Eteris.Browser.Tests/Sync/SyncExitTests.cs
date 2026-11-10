using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;
using Eteris.Sync.Model;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// As saídas da conta (T12): «Sair deste computador» (só local), «Retirar o acesso do Éteris à conta Google» (revoga),
// «Perdi um computador / foi roubado» e «Começar do zero…». A lógica é a de SyncExit (sem estado nem estáticos): os
// testes usam agentes de teste e delegados, nunca o SyncService nem a Google real.
public sealed class SyncExitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-sync-exit-" + Guid.NewGuid().ToString("N"));
    private int _stops;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private void StopHere() => _stops++;

    private static Func<CancellationToken, Task<RevokeOutcome>> Answer(RevokeOutcome outcome) => _ => Task.FromResult(outcome);

    // ---- três computadores numa pasta (ou no Drive falso) ------------------------------------------------------------

    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private long Now() => _now;
    private string Folder => Path.Combine(_root, "pasta");

    private sealed record Pc(SyncAgent Agent, SecretMemoryProfile Profile, MemoryStore Store);

    private Pc NewPc(string name, Func<ISyncTransport>? transport = null, MemoryStore? store = null, SecretMemoryProfile? profile = null)
    {
        profile ??= new SecretMemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => new FolderTransport(Folder)), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Pc(agent, profile, store);
    }

    private static async Task Settle(params Pc[] pcs)
    {
        for (var round = 0; round < 3; round++)
            foreach (var pc in pcs)
                Assert.True(await pc.Agent.RunAsync());
    }

    private async Task<Pc> JoinAsync(Pc first, string name, Func<ISyncTransport>? transport = null)
    {
        var pc = NewPc(name, transport);
        await pc.Agent.StartAsync();
        await pc.Agent.JoinWithCodeAsync(first.Agent.RevealKit()!.Words);
        return pc;
    }

    // «Casa», «Trabalho» e «Perdido» na mesma conta, com a lista de aparelhos lida na Casa.
    private async Task<(Pc Casa, Pc Trabalho, Pc Perdido)> ThreeAsync(Func<ISyncTransport>? casaTransport = null)
    {
        var casa = NewPc("Casa", casaTransport);
        await casa.Agent.StartAsync();
        var trabalho = await JoinAsync(casa, "Trabalho");
        var perdido = await JoinAsync(casa, "Perdido");
        await Settle(casa, trabalho, perdido);
        await casa.Agent.RefreshDevicesAsync();
        return (casa, trabalho, perdido);
    }

    private static string IdOf(Pc host, string name) => host.Agent.Status.Devices.Single(d => d.Name == name).Id;

    private const string Site = "https://contas.exemplo.pt";

    // A troca de chave falha a meio (o registo de chaves antigo não se apaga), como em SyncAgentTests.
    private sealed class FailingKeysTransport(ISyncTransport inner) : ISyncTransport
    {
        public bool FailDeletingKeys { get; set; }
        public string Name => inner.Name;
        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);
        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
            => FailDeletingKeys && path.EndsWith("/keys", StringComparison.Ordinal) ? throw new IOException("falha de propósito") : inner.DeleteAsync(path, cancellationToken);
    }

    private readonly List<ReauthPurpose> _asked = [];
    private bool _answer;

    private Task<bool> Confirm(ReauthPurpose purpose)
    {
        _asked.Add(purpose);
        return Task.FromResult(_answer);
    }

    // ---- textos -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Texts_AreThePlanTexts()
    {
        Assert.Equal("Sai só deste computador. A Google continua a listar o Éteris na sua conta até usar «Retirar o acesso».", SyncExit.SignOutHelp);
        Assert.Equal("Retirar o acesso do Éteris à conta Google", SyncExit.RevokeTitle);
        Assert.Equal("Revoga a autorização na Google e desliga todos os seus computadores. Também em myaccount.google.com/permissions.", SyncExit.RevokeText);
        Assert.Equal("Perdi um computador / foi roubado", SyncExit.LostTitle);
        Assert.Equal("Entre outra vez nos seus outros computadores (3 ações em cada) e guarde o kit novo: o antigo deixou de valer.", SyncExit.LostDone);
        Assert.Equal("A conta antiga na nuvem é apagada. O que só estava nela perde-se. O que está neste computador fica e passa para a conta nova.", SyncExit.StartOverText);
        Assert.Equal("O aparelho removido já tinha as suas palavras-passe: mude as dos sites importantes.", SyncExit.AdvicePasswords);
        Assert.Contains("ficam neste computador", SyncExit.SignOutKeeps);
    }

    // ---- «Retirar o acesso» -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_Confirmed_StopsHere_Once()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.Revoked), StopHere, CancellationToken.None);
        Assert.True(done);
        Assert.Equal(1, _stops);
        Assert.Equal(SyncExit.RevokeDone, message);
    }

    // R6: «invalid_token» só diz que o token DESTE computador já não valia; a autorização pode continuar viva.
    [Fact]
    public async Task AlreadyInvalid_NeverSaysWithdrawn()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.AlreadyInvalid), StopHere, CancellationToken.None);
        Assert.False(done);
        Assert.Equal(1, _stops); // a sessão daqui ficou apagada: a sincronização para aqui
        Assert.Equal(SyncExit.RevokeUnconfirmed, message);
        Assert.NotEqual(SyncExit.RevokeDone, message);
        foreach (var text in new[] { SyncExit.RevokeUnconfirmed, SyncExit.LostUnconfirmed })
        {
            Assert.DoesNotContain("foi retirado:", text);
            Assert.DoesNotContain(SyncExit.LostDone, text);
            Assert.Contains("myaccount.google.com/permissions", text);
            Assert.Contains("não foi possível confirmar", text);
            Assert.DoesNotContain("https://", text);
        }
        // Só a mensagem da revogação confirmada diz «foi retirado:» (LostHalfway diz «ainda NÃO foi retirado:»).
        var withdrawn = typeof(SyncExit).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                && System.Text.RegularExpressions.Regex.IsMatch((string)f.GetRawConstantValue()!, "(?<!não )foi retirado:"))
            .Select(f => f.Name).ToList();
        Assert.Equal([nameof(SyncExit.RevokeDone)], withdrawn);
    }

    [Fact]
    public async Task Revoke_Failed_DoesNotStop_AndSaysItWasNotWithdrawn()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.Failed), StopHere, CancellationToken.None);
        Assert.False(done);
        Assert.Equal(0, _stops);
        Assert.Contains("myaccount.google.com/permissions", message);
        Assert.Contains("não foi retirado", message);
        Assert.DoesNotContain("foi retirado:", message);

        // O limite de tempo (ou outro cancelamento) conta como falha.
        var (cancelled, cancelledMessage) = await SyncExit.RevokeAsync(_ => throw new OperationCanceledException(), StopHere, CancellationToken.None);
        Assert.False(cancelled);
        Assert.Equal(message, cancelledMessage);
        Assert.Equal(0, _stops);

        var (noSession, noSessionMessage) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.NotSignedIn), StopHere, CancellationToken.None);
        Assert.False(noSession);
        Assert.Equal(SyncExit.RevokeNoSession, noSessionMessage);
        Assert.Equal(0, _stops);
    }

    // ---- «Perdi um computador» ------------------------------------------------------------------------------------------

    // R8, R9: primeiro remove (a troca de chave acaba por inteiro), só depois revoga.
    [Fact]
    public async Task Lost_RemovesFirst_ThenRevokes()
    {
        var (casa, _, _) = await ThreeAsync();
        var kitBefore = casa.Agent.Status.Kit!.Id;
        var calls = 0;
        bool pendingAtRevoke = true;
        string? kitAtRevoke = null;
        IReadOnlySet<string>? secretsAtRevoke = null;
        var listedAtRevoke = new List<string>();
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, _ =>
        {
            calls++;
            pendingAtRevoke = casa.Agent.RotationPending;
            kitAtRevoke = casa.Agent.Status.Kit!.Id;
            secretsAtRevoke = casa.Agent.SecretCollectionsInAccount;
            listedAtRevoke.AddRange(casa.Agent.Status.Devices.Select(d => d.Name));
            return Task.FromResult(RevokeOutcome.Revoked);
        }, StopHere, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(pendingAtRevoke);
        Assert.NotEqual(kitBefore, kitAtRevoke);
        Assert.DoesNotContain("Perdido", listedAtRevoke);
        Assert.True(result.Removed);
        Assert.True(result.Revoked);
        Assert.Equal(1, _stops);
        Assert.Equal(SyncExit.LostMessage(secretsAtRevoke!), result.Message);
    }

    [Fact]
    public async Task Lost_RemovalFails_NeverRevokes()
    {
        var (casa, _, _) = await ThreeAsync();
        var calls = 0;
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Casa"), null, _ => { calls++; return Task.FromResult(RevokeOutcome.Revoked); },
            StopHere, CancellationToken.None);
        Assert.False(result.Removed);
        Assert.False(result.Revoked);
        Assert.Equal("Para tirar este computador da conta, use «Sair».", result.Message);
        Assert.Equal(0, calls);
        Assert.Equal(0, _stops);
    }

    // R9: com a troca de chave a meio, revogar deixava os outros computadores sem forma de receber a chave nova.
    [Fact]
    public async Task Lost_RotationLeftHalfway_NeverRevokes()
    {
        var flaky = new FailingKeysTransport(new FolderTransport(Folder));
        var (casa, trabalho, _) = await ThreeAsync(() => flaky);
        flaky.FailDeletingKeys = true;
        var calls = 0;
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, _ => { calls++; return Task.FromResult(RevokeOutcome.Revoked); },
            StopHere, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(0, _stops);
        Assert.False(result.Removed);
        Assert.False(result.Revoked);
        Assert.EndsWith(SyncExit.LostHalfway, result.Message);
        Assert.True(casa.Agent.RotationPending);

        // A ronda seguinte acaba a troca: ninguém perdeu a chave.
        flaky.FailDeletingKeys = false;
        Assert.True(await casa.Agent.RunAsync());
        Assert.False(casa.Agent.RotationPending);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
    }

    [Fact]
    public async Task Lost_RevokeFails_SaysRemovedButNotWithdrawn()
    {
        var (casa, _, _) = await ThreeAsync();
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, Answer(RevokeOutcome.Failed), StopHere, CancellationToken.None);
        Assert.True(result.Removed);
        Assert.False(result.Revoked);
        Assert.Equal(0, _stops);
        Assert.Equal(SyncExit.LostNotRevoked, result.Message);
    }

    // R14: a troca de chave só protege o que vem a seguir; o que o perdido já tinha continua lá.
    [Fact]
    public async Task Lost_TheLostDevice_KeepsWhatItHad_AndGetsNothingNew()
    {
        var (casa, trabalho, perdido) = await ThreeAsync();
        casa.Profile.Login(Site, "ana", "segredo-antigo");
        await Settle(casa, trabalho, perdido);
        Assert.Equal("segredo-antigo", perdido.Profile.Password(Site, "ana"));

        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, Answer(RevokeOutcome.Revoked), StopHere, CancellationToken.None);
        Assert.True(result.Removed);
        casa.Profile.Plain.Bookmark("https://depois.example/", "Depois");
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await perdido.Agent.RunAsync());
        Assert.Equal("segredo-antigo", perdido.Profile.Password(Site, "ana"));
        Assert.Null(perdido.Profile.Plain.Title("https://depois.example/"));
    }

    // Um computador no Drive falso com a sua sessão Google.
    private (Func<ISyncTransport> Transport, GoogleSession Session) Drive(FakeGoogle google)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(_root, "tokens-" + Guid.NewGuid().ToString("N")), new Plain()), Now);
        session.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        return (() => new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask), session);
    }

    private sealed class Plain : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    // R11: os outros ficam em «entrar outra vez» e, depois de entrarem, seguem sozinhos a chave nova.
    [Fact]
    public async Task Lost_OthersFollowTheNewKey_AfterSigningInAgain()
    {
        var google = new FakeGoogle();
        var (casaDrive, casaSession) = Drive(google);
        var (trabalhoDrive, _) = Drive(google);
        var (perdidoDrive, _) = Drive(google);
        var casa = NewPc("Casa", casaDrive);
        await casa.Agent.StartAsync();
        var trabalho = await JoinAsync(casa, "Trabalho", trabalhoDrive);
        var perdido = await JoinAsync(casa, "Perdido", perdidoDrive);
        await Settle(casa, trabalho, perdido);
        await casa.Agent.RefreshDevicesAsync();

        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, casaSession.RevokeAccessAsync,
            () => casa.Agent.SignOut(forgetKey: false), CancellationToken.None);
        Assert.True(result.Removed);
        Assert.True(result.Revoked);
        trabalho.Profile.Plain.Bookmark("https://trabalho.example/", "Do trabalho");
        Assert.False(await trabalho.Agent.RunAsync());
        Assert.True(trabalho.Agent.Status.NeedsSignIn);

        // Entrar outra vez nos dois (a Google volta a dar a autorização).
        google.Regrant();
        casaSession.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        await casa.Agent.StartAsync();
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Contains("mudou de chave", trabalho.Agent.Status.Notice);
        casa.Profile.Plain.Bookmark("https://casa.example/", "Da casa");
        await Settle(casa, trabalho);
        Assert.Equal("Da casa", trabalho.Profile.Plain.Title("https://casa.example/"));
        Assert.Equal("Do trabalho", casa.Profile.Plain.Title("https://trabalho.example/"));
    }

    // R11: neste computador, o kit novo fica por guardar, também depois de entrar outra vez.
    [Fact]
    public async Task Lost_TheNewKitIsStillPending_AfterReentry()
    {
        var (casa, _, _) = await ThreeAsync();
        casa.Agent.MarkKitSaved();
        var kitBefore = casa.Agent.Status.Kit!.Id;
        Assert.Equal(KitState.Saved, casa.Agent.Status.Kit.State);
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, Answer(RevokeOutcome.Revoked),
            () => casa.Agent.SignOut(forgetKey: false), CancellationToken.None);
        Assert.True(result.Revoked);
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        Assert.NotEqual(kitBefore, casa.Agent.Status.Kit!.Id);
        Assert.NotEqual(KitState.Saved, casa.Agent.Status.Kit.State);
    }

    [Fact]
    public async Task Lost_FolderMode_OnlyRemoves()
    {
        var (casa, _, _) = await ThreeAsync();
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, null, StopHere, CancellationToken.None);
        Assert.True(result.Removed);
        Assert.False(result.Revoked);
        Assert.Equal(0, _stops);
        Assert.StartsWith(SyncExit.LostFolderDone, result.Message);
        Assert.Contains(SyncExit.LostKeeps, result.Message);
        Assert.DoesNotContain(SyncExit.LostDone, result.Message);
        Assert.DoesNotContain(SyncExit.LostHere, result.Message);
    }

    // R14, R15: o que fica no perdido, o kit antigo morto, e este computador também saiu.
    [Fact]
    public void Lost_Message_SaysWhatStays_AndTheOldKitIsDead()
    {
        var message = SyncExit.LostMessage(new HashSet<string> { Collections.Logins });
        var done = message.IndexOf(SyncExit.LostDone, StringComparison.Ordinal);
        var here = message.IndexOf(SyncExit.LostHere, StringComparison.Ordinal);
        var keeps = message.IndexOf(SyncExit.LostKeeps, StringComparison.Ordinal);
        Assert.Equal(0, done);
        Assert.True(here > done && keeps > here, message);
        Assert.EndsWith(SyncExit.LostGoogle, message);
        Assert.Contains("o antigo deixou de valer", message);
        Assert.Contains("entre outra vez aqui", message);
        foreach (var promise in new[] { "apagad", "deixou de conseguir ler", "já não tem" })
            Assert.DoesNotContain(promise, message);
    }

    [Theory]
    [InlineData("logins", true, false)]
    [InlineData("cards", false, true)]
    [InlineData("ibans", false, true)]
    [InlineData("idDocs", false, true)]
    [InlineData("logins,cards", true, true)]
    [InlineData("", false, false)]
    [InlineData("contacts", false, false)]
    public void Lost_Advice_ByKindOfSecret(string kinds, bool passwords, bool cards)
    {
        var advice = SyncExit.LostAdvice(kinds.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(passwords, advice.Contains(SyncExit.AdvicePasswords, StringComparison.Ordinal));
        Assert.Equal(cards, advice.Contains(SyncExit.AdviceCards, StringComparison.Ordinal));
        if (passwords && cards)
            Assert.True(advice.IndexOf(SyncExit.AdvicePasswords, StringComparison.Ordinal) < advice.IndexOf(SyncExit.AdviceCards, StringComparison.Ordinal));
        if (!passwords && !cards) Assert.Equal(string.Empty, advice);
    }

    // O conjunto das coleções secretas lê-se antes de parar a sincronização aqui (parar esquece-o).
    [Fact]
    public async Task Lost_ReadsTheSecretKindsBeforeStopping()
    {
        var (casa, trabalho, perdido) = await ThreeAsync();
        casa.Agent.SetCollections([Collections.Cards, Collections.Ibans], on: true);
        casa.Profile.Login(Site, "ana", "segredo");
        casa.Profile.SetSecret(Collections.Cards, "c-casa", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 8, ["expYear"] = 2029 });
        await Settle(casa, trabalho, perdido);
        await casa.Agent.RefreshDevicesAsync();
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, Answer(RevokeOutcome.Revoked),
            () => casa.Agent.SignOut(forgetKey: false), CancellationToken.None);
        Assert.True(result.Revoked);
        Assert.Empty(casa.Agent.SecretCollectionsInAccount); // parou aqui: esquecido
        Assert.Contains(SyncExit.AdvicePasswords, result.Message);
        Assert.Contains(SyncExit.AdviceCards, result.Message);
    }

    // S27: a porta do Windows de «Perdi um computador» pede sempre, haja ou não segredos.
    [Fact]
    public async Task Lost_AlwaysAsksWindows()
    {
        var agent = NewPc("Casa").Agent;
        agent.SetCollections(agent.EnabledCollections.Where(Collections.IsSecret).ToList(), on: false);
        await agent.StartAsync();
        Assert.False(agent.HasSecrets);
        _answer = false;
        Assert.False(await SyncExit.MayLoseDeviceAsync(Confirm));
        Assert.Equal([ReauthPurpose.LostDevice], _asked);
        _answer = true;
        Assert.True(await SyncExit.MayLoseDeviceAsync(Confirm));
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.LostDevice));
        Assert.StartsWith("O Éteris quer ", ReauthPolicy.Message(ReauthPurpose.LostDevice));
    }

    [Fact]
    public void LostRoute_AsksWindowsOnce_AndIsNotTheRemoveRule()
    {
        var factory = GoogleSyncTests.Src("Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs");
        var route = GoogleSyncTests.CaseBlock(factory, "case \"/sync-lost\"");
        const string gate = "SyncExit.MayLoseDeviceAsync(purpose => ConfirmWithWindows(browserId, purpose))";
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(route, System.Text.RegularExpressions.Regex.Escape(gate)));
        Assert.DoesNotContain("MayRenewKeyAsync", route);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(factory,
            System.Text.RegularExpressions.Regex.Escape("SyncChoices.MayRenewKeyAsync(agent, purpose => ConfirmWithWindows(browserId, purpose))")).Count);
        var service = GoogleSyncTests.Src("Eteris.Browser", "Sync", "SyncService.cs");
        var body = GoogleSyncTests.Body(service, "public static void LostDevice(");
        Assert.Contains("SyncExit.LostDeviceAsync(", body);
        Assert.DoesNotContain("Act(", body);
    }

    [Fact]
    public void LostPanel_IsOnThePage()
    {
        var page = GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html");
        foreach (var text in new[] { "id=\"syncLost\"", SyncExit.LostTitle, SyncExit.LostStep1, SyncExit.LostStep2, SyncExit.LostStep3, "sync-lost", "syncLostWhich" })
            Assert.Contains(text, page);
        Assert.Equal("1. O aparelho sai da conta e a conta muda de chave: daqui em diante, esse computador não recebe nem consegue abrir nada novo. O que ele já tinha continua lá.", SyncExit.LostStep1);
        Assert.Equal("2. O acesso do Éteris à conta Google é retirado em todos os seus computadores: sem isto, o computador perdido ainda podia apagar ou estragar os ficheiros no Drive.", SyncExit.LostStep2);
        Assert.Equal("3. Depois, entra outra vez neste e nos seus outros computadores e guarda o kit novo: o antigo deixa de valer.", SyncExit.LostStep3);
        Assert.DoesNotContain("(ler, não consegue)", page);
    }

    // R6, R10: invalid_token em «Perdi um computador»: removido, mas sem confirmação da Google.
    [Fact]
    public async Task Lost_AlreadyInvalid_SaysRemovedButUnconfirmed()
    {
        var (casa, trabalho, perdido) = await ThreeAsync();
        casa.Profile.Login(Site, "ana", "segredo");
        await Settle(casa, trabalho, perdido);
        await casa.Agent.RefreshDevicesAsync();
        var pendingAtRevoke = true;
        var result = await SyncExit.LostDeviceAsync(casa.Agent, IdOf(casa, "Perdido"), null, _ =>
        {
            pendingAtRevoke = casa.Agent.RotationPending;
            return Task.FromResult(RevokeOutcome.AlreadyInvalid);
        }, StopHere, CancellationToken.None);
        Assert.False(pendingAtRevoke);
        Assert.True(result.Removed);
        Assert.False(result.Revoked);
        Assert.Equal(1, _stops);
        Assert.StartsWith(SyncExit.LostUnconfirmed, result.Message);
        Assert.Contains(SyncExit.LostKeeps, result.Message);
        Assert.Contains(SyncExit.AdvicePasswords, result.Message);
        foreach (var text in new[] { SyncExit.LostDone, SyncExit.LostHere, "foi retirado:", SyncExit.LostNotRevoked })
            Assert.DoesNotContain(text, result.Message);
    }

    // ---- «Começar do zero…» -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("começar do zero", true)]
    [InlineData(" Começar  do Zero ", true)]
    [InlineData("comecar do zero", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("apagar", false)]
    [InlineData("começar", false)]
    [InlineData("começar do zero já", false)]
    public void StartOver_WordMustMatch(string? typed, bool matches)
        => Assert.Equal(matches, SyncExit.StartOverWordMatches(typed));

    // As cadeias (contas) que estão no transporte, pelo registo de chaves.
    private async Task<List<string>> ChainsAsync()
    {
        var chains = new List<string>();
        foreach (var path in await new FolderTransport(Folder).ListAsync(SyncPaths.Root))
            if (SyncPaths.TryParseKeys(path, out var chain)) chains.Add(chain);
        return chains;
    }

    [Fact]
    public async Task StartOver_OnlyBeforeJoining()
    {
        var casa = NewPc("Casa");
        await casa.Agent.StartAsync();
        var chains = await ChainsAsync();
        Assert.Single(chains);
        var (done, message) = await SyncExit.StartOverAsync(casa.Agent, CancellationToken.None);
        Assert.False(done);
        Assert.Equal(SyncExit.StartOverWrongPhase, message);
        Assert.Equal(chains, await ChainsAsync());
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        Assert.True(casa.Agent.Enabled);
    }

    // «Casa» criou a conta; «Novo» (sem kit nem outro computador à mão) começa do zero a partir dos seus dados.
    private async Task<(Pc Casa, Pc Novo, string OldChain)> StartedOverAsync()
    {
        var casa = NewPc("Casa");
        casa.Profile.Plain.Bookmark("https://a.example/", "A");
        await casa.Agent.StartAsync();
        var oldChain = Assert.Single(await ChainsAsync());
        var novo = NewPc("Novo");
        novo.Profile.Plain.Bookmark("https://b.example/", "B");
        await novo.Agent.StartAsync();
        Assert.Contains(novo.Agent.Status.Phase, new[] { SyncPhase.Choose, SyncPhase.Pairing });

        var (done, message) = await SyncExit.StartOverAsync(novo.Agent, CancellationToken.None);
        Assert.True(done, message);
        Assert.Equal(SyncExit.StartOverDone, message);
        return (casa, novo, oldChain);
    }

    [Fact]
    public async Task StartOver_DeletesTheOldAccount_AndCreatesANewOne_KeepingLocalData()
    {
        var (casa, novo, oldChain) = await StartedOverAsync();
        Assert.Equal(SyncPhase.On, novo.Agent.Status.Phase);
        var chain = Assert.Single(await ChainsAsync());
        Assert.NotEqual(oldChain, chain);
        Assert.NotEqual(casa.Store.Load()!.Secret, novo.Store.Load()!.Secret);
        Assert.Equal("B", novo.Profile.Plain.Title("https://b.example/"));
        Assert.Null(novo.Profile.Plain.Title("https://a.example/"));
        Assert.Equal(KitState.Pending, novo.Agent.Status.Kit!.State);
    }

    // O outro computador vê a conta desaparecer: para, não pede nada sozinho e guarda a chave (o alerta é da T7).
    [Fact]
    public async Task StartOver_TheOtherComputer_StopsAndAsksNothingByItself()
    {
        var (casa, _, _) = await StartedOverAsync();
        await casa.Agent.RunAsync();
        Assert.Equal(SyncPhase.Stopped, casa.Agent.Status.Phase);
        Assert.Null(casa.Agent.Status.Pairing);
        Assert.True(casa.Agent.HasKey);
    }

    [Fact]
    public async Task StartOver_AsksWindowsAlways()
    {
        _answer = false;
        Assert.False(await SyncExit.MayStartOverAsync(Confirm));
        Assert.Equal([ReauthPurpose.StartOver], _asked);
        _answer = true;
        Assert.True(await SyncExit.MayStartOverAsync(Confirm));
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.StartOver));
        Assert.StartsWith("O Éteris quer ", ReauthPolicy.Message(ReauthPurpose.StartOver));
    }

    [Fact]
    public void StartOverRoute_ChecksTheWord_ThenWindows_ThenActs()
    {
        var factory = GoogleSyncTests.Src("Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs");
        var route = GoogleSyncTests.CaseBlock(factory, "case \"/sync-start-over\"");
        var word = route.IndexOf("StartOverWordMatches(", StringComparison.Ordinal);
        var windows = route.IndexOf("MayStartOverAsync(purpose => ConfirmWithWindows(browserId, purpose))", StringComparison.Ordinal);
        var act = route.IndexOf("SyncService.StartOver()", StringComparison.Ordinal);
        Assert.True(word >= 0 && windows > word && act > windows, route);
        var page = GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html");
        foreach (var text in new[] { "id=\"syncStartOver\"", "Não tem nada disto?", "Começar do zero…", SyncExit.StartOverText, "sync-start-over" })
            Assert.Contains(text, page);
        Assert.DoesNotContain("syncLostLink", page);
        Assert.DoesNotContain("Perdeu o código e não tem outro computador ligado?", page);
    }

    // ---- a página -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExitPage_HasTheTexts_AndTheNoteOutsideTheBody()
    {
        var page = GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html");
        foreach (var text in new[] { SyncExit.SignOutHelp, SyncExit.SignOutKeeps, SyncExit.RevokeTitle, SyncExit.RevokeText })
            Assert.Contains(text, page);
        var body = page.IndexOf("id=\"syncBody\"", StringComparison.Ordinal);
        Assert.True(body > 0);
        var help = page.IndexOf("id=\"syncOutHelp\"", StringComparison.Ordinal);
        var exitNote = page.IndexOf("id=\"syncExitNote\"", StringComparison.Ordinal);
        Assert.InRange(help, 0, body);
        Assert.InRange(exitNote, 0, body);
        Assert.Contains("id=\"googleButton\" aria-describedby=\"googleNote\"", page);
        var settingsPage = GoogleSyncTests.Src("Eteris.Browser", "Browser", "SettingsPage.cs");
        Assert.Matches(@"busy = [^\n]*ExitBusy", settingsPage);
    }
}
