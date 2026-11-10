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

    [Fact]
    public void Rows_AreThoseOfSection46_InOrder_WithTheDefaultsOfD1()
    {
        var rows = SyncChoices.Rows(SyncGroups.DefaultCollections);
        Assert.Equal(["Favoritos", "Espaços", "Definições", "Decisões do Escudo", "Notas", "Palavras-passe", "Pagamentos", "Informações de contacto",
            "Documentos de identificação", "Viagens", "Abas abertas", "Chaves de acesso"], rows.Select(r => r.Title));
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
        Assert.Equal(["payments", "documents"], rows.Where(r => r.Ask).Select(r => r.Id));
        Assert.Contains("ligar pede o Windows Hello", Text("payments"));
        Assert.Contains("desligar uma linha não apaga nada", SyncChoices.Intro);
        Assert.Contains("segunda cifra", SyncChoices.Intro);
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
        Assert.Contains("Com este código abre-se também o cofre de palavras-passe", html);
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
    }
}
