using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests.Sync;

// D4 (docs/palavras-passe.md): o tópico «O que passa entre PCs», o Windows Hello ao ligar Pagamentos e Documentos e ao
// aprovar um aparelho com segredos, e o perfil exportado (.eteris) sem segredos por omissão.
public sealed class SyncChoicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-sync-choices-" + Guid.NewGuid().ToString("N"));
    private readonly List<ReauthPurpose> _asked = [];
    private bool _answer;

    public SyncChoicesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<bool> Confirm(ReauthPurpose purpose)
    {
        _asked.Add(purpose);
        return Task.FromResult(_answer);
    }

    private SyncAgent Agent(SecretMemoryProfile? profile = null)
        => new(() => new FolderTransport(Path.Combine(_root, "pasta")), profile ?? new SecretMemoryProfile(), new MemoryStore(), "Casa", () => 1_762_560_000_000);

    // «Foi eu — juntar este computador» (conta substituída, T7): com segredos ligados NESTE computador (as palavras-passe
    // estão ligadas por omissão), o Windows confirma primeiro, sempre; sem nenhum, não pergunta.
    [Fact]
    public async Task ReplacedJoin_WithLocalSecrets_AsksWindows()
    {
        var agent = Agent();
        _answer = false;
        Assert.False(await SyncChoices.MayJoinReplacedAsync(agent, Confirm));
        Assert.Equal([ReauthPurpose.JoinReplacedAccount], _asked);
        _answer = true;
        Assert.True(await SyncChoices.MayJoinReplacedAsync(agent, Confirm));
        Assert.Equal(2, _asked.Count);
        agent.SetCollections(Collections.Secret, on: false);
        _asked.Clear();
        _answer = false;
        Assert.True(await SyncChoices.MayJoinReplacedAsync(agent, Confirm));
        Assert.Empty(_asked);
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.JoinReplacedAccount));
        Assert.Equal("O Éteris quer juntar este computador a outra conta de sincronização.", ReauthPolicy.Message(ReauthPurpose.JoinReplacedAccount));
    }

    [Fact]
    public void Rows_AreThoseOfSection46_InOrder_WithTheDefaultsOfD1()
    {
        var rows = SyncChoices.Rows(SyncGroups.DefaultCollections);
        // 0.2.15 (T10): a escolha da conta entra imediatamente antes de «Pagamentos».
        Assert.Equal(["Favoritos", "Espaços", "Definições", "Decisões do Escudo", "Notas", "Palavras-passe", "Também pagamentos e documentos de identificação",
            "Pagamentos", "Informações de contacto", "Documentos de identificação", "Viagens", "Abas abertas", "Chaves de acesso"], rows.Select(r => r.Title));
        string Text(string id) => rows.Single(r => r.Id == id).Text;
        bool? On(string id) => rows.Single(r => r.Id == id).On;
        Assert.StartsWith("Palavras-passe, notas e sites onde nunca guardar", Text("passwords"));
        Assert.StartsWith("Cartões (sem o código de segurança) e IBAN", Text("payments"));
        Assert.StartsWith("Nomes, moradas, e-mails e telefones", Text("contacts"));
        // Os dois nomes de cada documento (Portugal e Brasil), como no desenho.
        Assert.StartsWith("Passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF", Text("documents"));
        Assert.StartsWith("Veículos e programas de fidelidade", Text("travel"));
        Assert.Equal("Só as que fazem sentido noutro computador", Text("settings"));
        Assert.Equal("Não passam: ficam no Windows Hello ou na chave de segurança", Text("passkeys"));
        foreach (var id in new[] { "bookmarks", "spaces", "settings", "shield", "notes", "passwords", "contacts", "travel" }) Assert.True(On(id), id);
        Assert.False(On("payments"));
        Assert.False(On("documents"));
        Assert.Null(On("passkeys"));
        Assert.Null(On("tabs"));
        Assert.Equal(["account", "payments", "documents"], rows.Where(r => r.Ask).Select(r => r.Id));
        Assert.False(On("account"));
        Assert.Equal(SyncChoices.AccountText, Text("account"));
        Assert.Contains("ligar pede o Windows Hello", Text("payments"));
        Assert.Contains("desligar uma linha não apaga nada", SyncChoices.Intro);
        Assert.Contains("segunda cifra", SyncChoices.Intro);

        // Com a escolha ligada na conta e os dois grupos aqui: ligada, e diz onde e quando (informação, não prova).
        var ms = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var day = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var both = SyncGroups.DefaultCollections.Concat(SyncGroups.AccountCollections).ToHashSet(StringComparer.Ordinal);
        var account = SyncChoices.Rows(both, new SyncChoices.AccountChoice(true, "Casa", ms)).Single(r => r.Id == "account");
        Assert.True(account.On);
        Assert.EndsWith($" Ligado em «Casa» em {day}.", account.Text);
        Assert.StartsWith(SyncChoices.AccountText, account.Text);
        Assert.Equal($" Ligado em {day}.", SyncChoices.AccountSince(null, ms));
        // Só um dos grupos aqui: a linha está desligada (neste computador falta o outro).
        var one = SyncGroups.DefaultCollections.Concat(["cards", "ibans"]).ToHashSet(StringComparer.Ordinal);
        Assert.False(SyncChoices.Rows(one, new SyncChoices.AccountChoice(true, "Casa", ms)).Single(r => r.Id == "account").On);
        // A conta sem a escolha, mesmo com os dois grupos aqui: desligada, sem «Ligado em».
        var off = SyncChoices.Rows(both, new SyncChoices.AccountChoice(false, "Casa", ms)).Single(r => r.Id == "account");
        Assert.False(off.On);
        Assert.Equal(SyncChoices.AccountText, off.Text);
        Assert.Null(SyncChoices.AccountChoice.Of(null));
    }

    // Os textos da secção 3 do brief da T10, carácter a carácter (os que o plano dá à letra).
    [Fact]
    public void Texts_SayCardsAndDocuments_WhenTheAccountSyncsThem()
    {
        Assert.Equal("as suas palavras-passe, cartões e documentos", SyncChoices.Receives(true));
        Assert.Equal("as suas palavras-passe", SyncChoices.Receives(false));
        Assert.Equal(PairingBarModel.DefaultReceives, SyncChoices.Receives(false));
        Assert.Equal("Recebeu favoritos, palavras-passe, cartões e documentos", SyncChoices.ReceivedText(true));
        Assert.Null(SyncChoices.ReceivedText(false));
        Assert.Equal("Também pagamentos e documentos de identificação", SyncChoices.AccountLine);
        Assert.Equal("Também pagamentos e documentos…", SyncChoices.AccountLink);
        Assert.Equal("Os seus outros computadores sincronizam agora pagamentos e documentos. Receber também neste?", SyncChoices.OfferText);
        Assert.Equal("Sim", SyncChoices.OfferYes);
        Assert.Equal("Não", SyncChoices.OfferNo);
        Assert.Equal("", SyncChoices.AccountSince(null, 0));
        // «since» recebido fora do intervalo de datas: sem «Ligado em», sem exceção.
        Assert.Equal("", SyncChoices.AccountSince(null, long.MaxValue));
        Assert.Equal("", SyncChoices.AccountSince("x", 1_762_560_000_000_000));
        Assert.Equal("", SyncChoices.AccountSince("x", -1));
        Assert.Equal("Um computador quer entrar na sua conta e receber as suas palavras-passe, cartões e documentos",
            PairingBarModel.Title(SyncChoices.Receives(true)));

        // O estado da página: a escolha da conta, a oferta e, por aparelho, se recebe (null num Éteris antigo).
        var status = TestStatus.Of(SyncPhase.On) with
        {
            AccountSecrets = true,
            SecretsOffer = true,
            Devices =
            [
                new DeviceInfo("a", "Casa", "pc", 0, true) { PaymentsAndDocuments = true },
                new DeviceInfo("b", "Trabalho", "pc", 0, false) { PaymentsAndDocuments = false },
                new DeviceInfo("c", "Antigo", "pc", 0, false),
            ],
        };
        var json = System.Text.Json.JsonSerializer.Serialize(SyncService.StatusData(status, 1_762_560_000_000));
        Assert.Contains("\"accountSecrets\":true", json);
        Assert.Contains("\"offer\":true", json);
        Assert.Contains("\"receivesSecrets\":true", json);
        Assert.Contains("\"receivesSecrets\":false", json);
        Assert.Contains("\"receivesSecrets\":null", json);
    }

    // Os ganchos do estado «Sincronizado.» da barra (T4): o aviso de herança no computador novo, e a ligação discreta
    // no computador que criou a conta.
    [Fact]
    public void SyncedHooks_Table()
    {
        Assert.Equal((null, null), SyncChoices.SyncedHooks(null));

        var inherited = SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Joined, InheritedSecrets = true, SecretsHere = true, AccountSecrets = true });
        Assert.Equal("Recebeu favoritos, palavras-passe, cartões e documentos", inherited.Text);
        Assert.Equal(new SignInBarButton("Abrir Definições", SignInBarAction.OpenSettings), inherited.Link);

        // Herdou mas a pessoa já desligou aqui: nada a dizer.
        Assert.Equal((null, null), SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Joined, InheritedSecrets = true, AccountSecrets = true }));

        var created = SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Created });
        Assert.Null(created.Text);
        Assert.Equal(new SignInBarButton("Também pagamentos e documentos…", SignInBarAction.AccountSecrets), created.Link);

        Assert.Equal((null, null), SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Created, AccountSecrets = true }));
        Assert.Equal((null, null), SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Created, SecretsHere = true }));
        Assert.Equal((null, null), SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On) with { Entry = SyncEntry.Joined }));
        Assert.Equal((null, null), SyncChoices.SyncedHooks(TestStatus.Of(SyncPhase.On)));

        // Na barra: o texto substitui «Sincronizado.» e o botão fica 12 s.
        var view = SignInBarModel.For(new SignInBarInput(new SignInProgress(SignInStage.Done, null, null, 1), TestStatus.Of(SyncPhase.On, last: 2), "Ana Teste", false)
        {
            SyncedText = inherited.Text,
            SyncedLink = inherited.Link,
        })!;
        Assert.Equal(SignInBarKind.Synced, view.Kind);
        Assert.Equal("Recebeu favoritos, palavras-passe, cartões e documentos", view.Text);
        Assert.Equal(SignInBarModel.SyncedLinkHold, view.HideAfter);
    }

    // A oferta e os ganchos passam pelo árbitro da T4 (uma barra de cada vez) e pelo controlo partilhado; nada de
    // barras novas nem de Visibility à mão, e o arranque continua sem a sincronização.
    [Fact]
    public void Bars_UseTheAccountChoice_ThroughTheArbiter()
    {
        string Src(params string[] parts) => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts])).Replace("\r\n", "\n");
        var service = Src("Sync", "SyncService.cs");
        var prompt = service[service.IndexOf("public static bool SecretsOfferPrompt", StringComparison.Ordinal)..];
        prompt = prompt[..prompt.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("Agent()", prompt);
        Assert.Contains("_agent?.SecretsOffer", prompt);

        var glue = Src("MainWindow.SignInBar.cs");
        Assert.Contains("RegisterBar(Sync.WindowBar.SecretsOffer", glue);
        Assert.Contains("SyncChoices.SyncedHooks(", glue);
        Assert.Contains("SignInBarAction.OfferYes", glue);
        Assert.Contains("VaultFrontToken()", glue);
        // Registada depois de «if (IsPrivate) return;» e antes do RefreshBars() final.
        var wire = glue[glue.IndexOf("private void WireSignInBar()", StringComparison.Ordinal)..];
        wire = wire[..wire.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.True(wire.IndexOf("if (IsPrivate) return;", StringComparison.Ordinal) < wire.IndexOf("RegisterBar(Sync.WindowBar.SecretsOffer", StringComparison.Ordinal));
        Assert.True(wire.IndexOf("RegisterBar(Sync.WindowBar.SecretsOffer", StringComparison.Ordinal) < wire.LastIndexOf("RefreshBars();", StringComparison.Ordinal));

        var window = Src("MainWindow.xaml.cs");
        Assert.DoesNotContain("SecretsOffer", window);
        Assert.DoesNotContain("SyncService", window);
        Assert.DoesNotContain("Visibility =", Src("Sync", "SyncChoices.cs"));
        Assert.DoesNotContain("Visibility =", Src("Sync", "SignInBarModel.cs"));
    }

    // R11: preencher e «Mostrar» continuam a pedir o Windows Hello como antes; a escolha da conta só pede SyncSecrets.
    [Fact]
    public async Task FillAndReveal_StillAskWindowsHello()
    {
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillPayment, false, true));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillDocument, false, true));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.Reveal, false, false));
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.SyncSecrets));

        var agent = new SyncAgent(() => new FolderTransport(Path.Combine(_root, "pasta")), new SecretMemoryProfile(), new MemoryStore(), "Casa", () => 1_762_560_000_000);
        await agent.StartAsync();
        _answer = true;
        Assert.Null(await SyncChoices.ChooseAccountAsync(agent, Confirm));
        Assert.Equal([ReauthPurpose.SyncSecrets], _asked);
    }

    [Theory]
    [InlineData("payments", "cards", "ibans")]
    [InlineData("documents", "idDocs", null)]
    public async Task EnablingPaymentsOrDocuments_AsksWindows_Always_AndRefusedStaysOff(string group, string first, string? second)
    {
        var agent = Agent();
        _answer = false;
        var error = await SyncChoices.ChooseAsync(agent, group, on: true, Confirm);
        Assert.Equal([ReauthPurpose.SyncSecrets], _asked);
        Assert.Contains("Não foi confirmado com o Windows", error);
        Assert.DoesNotContain(first, agent.EnabledCollections);

        _answer = true;
        Assert.Null(await SyncChoices.ChooseAsync(agent, group, on: true, Confirm));
        Assert.Equal(2, _asked.Count);
        Assert.Contains(first, agent.EnabledCollections);
        if (second is not null) Assert.Contains(second, agent.EnabledCollections);
        Assert.True(SyncChoices.Rows(agent.EnabledCollections).Single(r => r.Id == group).On);

        // Desligar não pede nada (e não apaga nada: SyncSecretProfileTests).
        Assert.Null(await SyncChoices.ChooseAsync(agent, group, on: false, Confirm));
        Assert.Equal(2, _asked.Count);
        Assert.DoesNotContain(first, agent.EnabledCollections);
        // SyncSecrets nunca usa a janela de graça.
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.SyncSecrets));
    }

    [Fact]
    public async Task TheOtherRows_DoNotAsk_AndAnUnknownRowDoesNothing()
    {
        var agent = Agent();
        foreach (var group in new[] { "passwords", "contacts", "travel", "notes", "bookmarks" })
        {
            Assert.Null(await SyncChoices.ChooseAsync(agent, group, on: false, Confirm));
            Assert.Null(await SyncChoices.ChooseAsync(agent, group, on: true, Confirm));
        }
        Assert.Empty(_asked);
        Assert.Equal("Esta linha não existe.", await SyncChoices.ChooseAsync(agent, "tabs", on: true, Confirm));
        Assert.Equal(SyncGroups.DefaultCollections.Order(StringComparer.Ordinal), agent.EnabledCollections.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApprovingADevice_WithSecretsInPlay_AsksWindows()
    {
        var agent = Agent();
        _answer = false;
        Assert.True(agent.HasSecrets); // as palavras-passe estão ligadas por omissão
        Assert.False(await SyncChoices.MayApproveAsync(agent, Confirm));
        Assert.Equal([ReauthPurpose.ApproveDevice], _asked);
        _answer = true;
        Assert.True(await SyncChoices.MayApproveAsync(agent, Confirm));
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.ApproveDevice));
        // O código novo segue a mesma regra.
        _answer = false;
        _asked.Clear();
        Assert.False(await SyncChoices.MayRenewKeyAsync(agent, Confirm));
        Assert.Equal([ReauthPurpose.ApproveDevice], _asked);
        _answer = true;

        // Sem nenhuma coleção do cofre ligada nem segredos na conta: aprova sem pedir.
        agent.SetCollections(Collections.Secret, on: false);
        Assert.False(agent.HasSecrets);
        _asked.Clear();
        Assert.True(await SyncChoices.MayApproveAsync(agent, Confirm));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task TheExportedProfile_HasNoSecretsByDefault_AndThemOnlyAfterWindows()
    {
        var collected = 0;
        IReadOnlyList<ArchiveSecret> Collect()
        {
            collected++;
            return [new ArchiveSecret("logins", new SecretItem("https://a.exemplo.test\nu", new JsonObject { ["origin"] = "https://a.exemplo.test", ["username"] = "u", ["password"] = "Inventada-Export-1" }))];
        }
        var (ok, none) = await SyncChoices.SecretsForExportAsync(false, Confirm, Collect);
        Assert.True(ok);
        Assert.Null(none);
        Assert.Empty(_asked);
        Assert.Equal(0, collected);

        _answer = false;
        var (refused, nothing) = await SyncChoices.SecretsForExportAsync(true, Confirm, Collect);
        Assert.False(refused);
        Assert.Null(nothing);
        Assert.Equal(0, collected); // nem se decifrou nada
        Assert.Equal([ReauthPurpose.ExportProfileSecrets], _asked);

        _answer = true;
        var (yes, secrets) = await SyncChoices.SecretsForExportAsync(true, Confirm, Collect);
        Assert.True(yes);
        Assert.Single(secrets!);
        Assert.True(ReauthPolicy.IsAlways(ReauthPurpose.ExportProfileSecrets));
    }

    [Fact]
    public void Archive_WithSecrets_SealsThemInside_AndOnlyTheCodeOpensThem()
    {
        using var secret = AccountSecret.Create();
        var records = new List<SyncRecord> { ProfileArchive.Record(Collections.Bookmarks, "https://pt.wikipedia.org/", new JsonObject { ["title"] = "Wikipédia" }, 1) };
        var vault = new List<ArchiveSecret>
        {
            new("logins", new SecretItem("https://a.exemplo.test\nu", new JsonObject { ["origin"] = "https://a.exemplo.test", ["username"] = "u", ["password"] = "Inventada-Arq-1" })),
            new("cards", new SecretItem("c1", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 8, ["expYear"] = 2029 })),
        };
        var plain = ProfileArchive.Export(records, secret, "Casa", 1);
        var (p0, s0) = ProfileArchive.ImportAll(plain, secret.ToCode());
        Assert.Single(p0.Records);
        Assert.Empty(s0);

        var file = ProfileArchive.Export(records, secret, "Casa", 1, vault);
        var (payload, opened) = ProfileArchive.ImportAll(file, secret.ToCode());
        Assert.Single(payload.Records); // os selados não ficam entre os registos normais
        Assert.Equal(2, opened.Count);
        Assert.Equal("Inventada-Arq-1", opened.Single(s => s.Collection == "logins").Item.Data["password"]!.GetValue<string>());
        Assert.Equal("4111111111111111", opened.Single(s => s.Collection == "cards").Item.Data["number"]!.GetValue<string>());
        using var other = AccountSecret.Create();
        Assert.Throws<WrongCodeException>(() => ProfileArchive.ImportAll(file, other.ToCode()));
        Assert.Throws<ArgumentException>(() => ProfileArchive.Export(records, secret, "Casa", 1, [new ArchiveSecret("bookmarks", new SecretItem("x", new JsonObject()))]));
        Assert.Throws<ArgumentException>(() => ProfileArchive.Export(records, secret, "Casa", 1,
            [new ArchiveSecret("cards", new SecretItem("c", new JsonObject { ["number"] = "4111111111111111", ["cvc"] = "1" }))]));
    }

    [Fact]
    public void VaultItems_GoOutAndComeIn_TheFileWins_AndTheOldOneStaysInTheHistory()
    {
        var a = new VaultService(Path.Combine(_root, "a"), new Dpapi(VaultKey.Purpose));
        var b = new VaultService(Path.Combine(_root, "b"), new Dpapi(VaultKey.Purpose));
        try
        {
            Assert.Empty(ProfileTransfer.CollectSecrets(a, () => false));
            Assert.False(File.Exists(Path.Combine(_root, "a", VaultKey.FileName))); // sem cofre, nada se cria

            a.SaveLogin(new LoginItem("", "https://conta.exemplo.test/", "u", "Do-Ficheiro-1", null, default, default, null, null, false, null));
            a.Save(new CardItem("", "Titular", "4111111111111111", 8, 2029, null, default, null));
            a.Save(new IdDocItem("", IdDocType.TaxId, "123456789", "Titular", "PT", null, null, null, null));
            var bId = b.SaveLogin(new LoginItem("", "https://conta.exemplo.test/", "u", "Deste-Computador-2", null, default, default, null, null, false, null));

            var secrets = ProfileTransfer.CollectSecrets(a, () => true);
            Assert.Equal(3, secrets.Count);
            using var code = AccountSecret.Create();
            var (_, opened) = ProfileArchive.ImportAll(ProfileArchive.Export([], code, "A", 1, secrets), code.ToCode());
            var (applied, replaced) = ProfileTransfer.ApplySecrets(b, opened);
            Assert.Equal(3, applied);
            Assert.Equal(1, replaced);
            Assert.Equal("Do-Ficheiro-1", b.GetLogin(bId)!.Password);
            Assert.Equal("Deste-Computador-2", b.PreviousPassword(bId));
            Assert.Single(b.List(VaultCollection.Cards));
            Assert.Single(b.List(VaultCollection.IdDocs));
            var summary = ProfileTransfer.Summary(new ProfileTransfer.Imported(0, 0, 0, 0, false, "A") { Vault = applied, VaultReplaced = replaced });
            Assert.Contains("3 itens do cofre", summary);
            Assert.Contains("a anterior ficou no histórico", summary);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void SettingsPage_HasTheTopic_TheExportBox_AndWhatTheCodeOpens()
    {
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("id=\"syncTopic\"", html);
        Assert.Contains("O que passa entre PCs", html);
        Assert.Contains("\"choices\":", html);
        Assert.Contains("Documentos de identificação", html);
        Assert.Contains("id=\"exportSecrets\"", html);
        Assert.Contains("Incluir palavras-passe, pagamentos e documentos", html);
        Assert.Contains("abre também o cofre de palavras-passe", html);
        Assert.Contains("esqueci-me do kit", SyncChoices.Intro);
        // Aprovar um computador novo faz-se na barra da janela, escrevendo o código (0.2.15, T8): as Definições só
        // dizem onde responder e já não têm caminho para aprovar.
        Assert.Contains("Responda na barra que aparece no cimo da janela do Éteris", html);
        Assert.DoesNotContain("sync-approve", html);
        // A caixa vem desmarcada.
        Assert.DoesNotContain("id=\"exportSecrets\" checked", html);

        // As rotas passam pelas decisões testadas aqui (e a confirmação vem do Windows, com a janela da aba).
        var routes = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("SyncChoices.ChooseAsync(agent, group, on, purpose => ConfirmWithWindows(browserId, purpose))", routes);
        // A rota que aprovava sem código saiu (D11): aprovar faz-se só na barra da janela, escrevendo o código, e a
        // porta do Windows está no serviço, depois do código certo.
        Assert.DoesNotContain("\"/sync-approve\"", routes);
        Assert.Contains("SyncChoices.MayApproveAsync(agent, confirm)",
            File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs")));
        // Revisão: um código novo e remover um aparelho (que mostra um código novo) também pedem o Windows com segredos.
        Assert.Equal(2, routes.Split("SyncChoices.MayRenewKeyAsync(agent, purpose => ConfirmWithWindows(browserId, purpose))").Length - 1);
        Assert.DoesNotContain("case \"/sync-new-code\":\n                Sync.SyncService.Act", routes.Replace("\r\n", "\n"));
        Assert.StartsWith("Esse computador vai receber", SyncChoices.ApproveWarning);
        Assert.Contains("SyncChoices.SecretsForExportAsync(true, purpose => ConfirmWithWindows(browserId, purpose)", routes);

        // A oferta única «receber também neste?» (T10): rota com a confirmação do Windows da aba, no serviço, e um item
        // próprio nas Definições, fora do tópico (a T14 esconde o tópico em «Mais opções» e deixa a oferta à vista).
        Assert.Contains("case \"/sync-offer\"", routes);
        Assert.Contains("SyncService.AnswerSecretsOfferAsync(accept, purpose => ConfirmWithWindows(browserId, purpose))", routes);
        var service = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.Contains("SyncChoices.AnswerOfferAsync(agent, accept, confirm)", service);
        var page = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("id=\"syncOfferItem\"", page);
        Assert.Contains("sync-offer", page);
        Assert.Contains("receivesSecrets", page);
        Assert.True(page.IndexOf("id=\"syncOfferItem\"", StringComparison.Ordinal) < page.IndexOf("id=\"syncTopicItem\"", StringComparison.Ordinal));
        Assert.Contains(SyncChoices.OfferText, page);
        Assert.Contains("<small>(Windows Hello)</small>", page);
        // O pedido de aprovação diz «cartões e documentos» quando a conta os sincroniza (R10): a barra lê a escolha do
        // agente que já existe (o campo, nunca Agent()).
        var bar = service[service.IndexOf("internal static PairingBarView PairingBarState()", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("SyncChoices.Receives(_agent?.AccountSecrets == true)", bar);
        Assert.DoesNotContain("PairingBarModel.DefaultReceives", bar);
    }
}
