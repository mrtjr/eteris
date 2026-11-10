using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Pagamentos e documentos como escolha da conta (0.2.15, T10, D4′): um só Windows Hello liga os dois grupos e grava a
// escolha na conta; um computador novo que entra depois herda-a sem pedir nada; os que já estavam ligados (ou que já
// estiveram numa conta) só recebem a oferta única. Tudo numa pasta temporária, sem rede nem conta Google.
public sealed class SyncAccountChoiceTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-account-choice-" + Guid.NewGuid().ToString("N"));
    private readonly List<ReauthPurpose> _asked = [];
    private bool _answer = true;

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private Task<bool> Confirm(ReauthPurpose purpose)
    {
        lock (_asked) _asked.Add(purpose);
        return Task.FromResult(_answer);
    }

    private sealed record Device(SyncAgent Agent, SecretMemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, MemoryStore? store = null, SecretMemoryProfile? profile = null)
    {
        profile ??= new SecretMemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(() => new FolderTransport(_folder), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Device(agent, profile, store);
    }

    // «Fechar e reabrir o Éteris»: um agente novo sobre o mesmo estado guardado e o mesmo perfil.
    private Device Reopen(Device device, string name) => NewDevice(name, device.Store, device.Profile);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 3000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "À espera de: " + what);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    private const string CardKey = "c-casa";
    private const string DocKey = "d-casa";

    private static void SaveCardAndDoc(Device device, string card = CardKey, string doc = DocKey)
    {
        device.Profile.SetSecret(Collections.Cards, card, new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 8, ["expYear"] = 2029 });
        device.Profile.SetSecret(Collections.IdDocs, doc, new JsonObject { ["type"] = "taxId", ["number"] = "529.982.247-25", ["country"] = "BR" });
    }

    private static bool HasAccountGroups(Device device) => SyncGroups.AccountCollections.All(device.Agent.EnabledCollections.Contains);

    private static bool HasAnyAccountGroup(Device device) => SyncGroups.AccountCollections.Any(device.Agent.EnabledCollections.Contains);

    private static int TouchedAccountCollections(Device device)
        => SyncGroups.AccountCollections.Sum(c => device.Profile.Touched.GetValueOrDefault(c));

    private async Task<Device> CreateAsync(string name = "Casa")
    {
        var device = NewDevice(name);
        await device.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, device.Agent.Status.Phase);
        return device;
    }

    // Entra pelo kit de recuperação do anfitrião (como no ecrã de entrada).
    private async Task<Device> JoinByKitAsync(Device host, string name, Device? device = null)
    {
        device ??= NewDevice(name);
        if (device.Agent.Status.Phase is SyncPhase.Off or SyncPhase.Stopped) await device.Agent.StartAsync();
        await device.Agent.JoinWithCodeAsync(host.Agent.RevealKit()!.Words);
        return device;
    }

    // Entra por aprovação no anfitrião, escrevendo lá o código que o computador novo mostra (T8).
    private async Task<Device> JoinByApprovalAsync(Device host, string name, Device? device = null)
    {
        device ??= NewDevice(name);
        if (device.Agent.Status.Phase is SyncPhase.Off or SyncPhase.Stopped) await device.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, device.Agent.Status.Phase);
        device.Agent.RequestPairing();
        for (var i = 0; i < 3000; i++)
        {
            await host.Agent.CheckRequestsAsync();
            if (host.Agent.Status.Requests.Count == 1 && device.Agent.Status.Pairing?.Code is not null) break;
            await Task.Delay(10);
        }
        Assert.Single(host.Agent.Status.Requests);
        var code = device.Agent.Status.Pairing!.Code!;
        device.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await host.Agent.ApproveWithCodeAsync(host.Agent.Status.Requests[0].Session, code));
        await Until(() => device.Agent.Status is { Phase: SyncPhase.On, Running: false, JoinedNow: false }, name + " ligado depois da aprovação");
        return device;
    }

    private async Task<(Device Casa, Device Trabalho)> TwoAsync()
    {
        var casa = await CreateAsync();
        var trabalho = await JoinByKitAsync(casa, "Trabalho");
        await Settle(casa, trabalho);
        return (casa, trabalho);
    }

    // Escreve no registo da conta o que um aparelho estranho (ou um Éteris com outro valor) escreveria: um motor de
    // teste com o segredo da conta.
    private async Task WriteAccountRecordAsync(Device host, JsonObject data)
    {
        _now += 1000;
        using var secret = AccountSecret.FromCode(host.Agent.RevealKit()!.Words);
        var engine = await SyncEngine.JoinAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Motor de teste"), Now);
        await engine.PullAsync();
        engine.Put(Collections.Settings, Collections.AccountSecretsKey, data);
        await engine.PushAsync();
        _now += 1000;
    }

    private static string Refused => SyncChoices.NotConfirmed("«Também pagamentos e documentos de identificação» continua desligado");

    // ---- 1 a 3: a ação --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountChoice_TwoGroups_OneWindowsHello()
    {
        var casa = await CreateAsync();
        _answer = true;
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.Equal([ReauthPurpose.SyncSecrets], _asked);
        foreach (var c in new[] { "cards", "ibans", "idDocs" }) Assert.Contains(c, casa.Agent.EnabledCollections);
        Assert.True(casa.Agent.AccountSecrets);
        Assert.Equal(["cards", "ibans", "idDocs"], SyncGroups.AccountCollections.Order(StringComparer.Ordinal));
        Assert.Equal(["payments", "documents"], SyncGroups.AccountGroups);
    }

    [Fact]
    public async Task AccountChoice_RefusedHello_NothingChanges()
    {
        var (casa, trabalho) = await TwoAsync();
        var saves = casa.Store.Saves;
        _answer = false;
        Assert.Equal(Refused, await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.Equal([ReauthPurpose.SyncSecrets], _asked);
        Assert.False(HasAnyAccountGroup(casa));
        Assert.Equal(saves, casa.Store.Saves); // nada escrito
        Assert.False(casa.Agent.HasLocalChanges());
        await Settle(casa, trabalho);
        Assert.False(casa.Agent.AccountSecrets);
        Assert.False(trabalho.Agent.AccountSecrets);
    }

    [Fact]
    public async Task AccountChoice_SyncOff_RefusesWithoutAskingWindows()
    {
        const string off = "A sincronização não está ligada neste computador.";
        var sem = NewDevice("Sem conta");
        Assert.Equal(off, await SyncChoices.ChooseAccountAsync(sem.Agent, Confirm));
        Assert.Throws<SyncUserException>(() => sem.Agent.SetAccountSecrets(true));

        // Ligada mas ainda sem chave: o transporte já tem a conta de outro (fase Choose).
        await CreateAsync();
        var novo = NewDevice("Novo");
        await novo.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.Equal(off, await SyncChoices.ChooseAccountAsync(novo.Agent, Confirm));
        Assert.Equal(off, await SyncChoices.ChooseAsync(novo.Agent, SyncChoices.AccountRow, on: true, Confirm));

        Assert.Empty(_asked);
        Assert.False(HasAnyAccountGroup(sem));
        Assert.False(HasAnyAccountGroup(novo));
    }

    // ---- 4 a 6: quem entra depois herda; quem entrou antes não ---------------------------------------------------

    [Fact]
    public async Task JoinsAfter_ByApproval_Inherits_WithoutHello()
    {
        var casa = await CreateAsync();
        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.True(await casa.Agent.RunAsync());
        _asked.Clear();

        var novo = await JoinByApprovalAsync(casa, "Novo");
        // Logo que a entrada acaba (sem ronda extra): os dois grupos ligados e os segredos no cofre.
        Assert.True(HasAccountGroups(novo));
        Assert.NotNull(novo.Profile.Secret(Collections.Cards, CardKey));
        Assert.NotNull(novo.Profile.Secret(Collections.IdDocs, DocKey));
        Assert.False(novo.Agent.Status.SecretsOffer);
        Assert.False(novo.Agent.SecretsOffer);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task JoinsAfter_ByKit_Inherits()
    {
        var casa = await CreateAsync();
        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.True(await casa.Agent.RunAsync());
        _asked.Clear();

        var novo = await JoinByKitAsync(casa, "Novo");
        Assert.True(HasAccountGroups(novo));
        Assert.NotNull(novo.Profile.Secret(Collections.Cards, CardKey));
        Assert.NotNull(novo.Profile.Secret(Collections.IdDocs, DocKey));
        Assert.False(novo.Agent.Status.SecretsOffer);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task JoinsBeforeTheChoice_DoesNotInheritLater()
    {
        var casa = await CreateAsync();
        var novo = await JoinByKitAsync(casa, "Novo");
        await Settle(casa, novo);
        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, novo);

        Assert.True(novo.Agent.AccountSecrets);
        Assert.False(HasAnyAccountGroup(novo));
        Assert.Null(novo.Profile.Secret(Collections.Cards, CardKey));
        Assert.Null(novo.Profile.Secret(Collections.IdDocs, DocKey));
        Assert.True(novo.Agent.Status.SecretsOffer); // oferta, não herança
        Assert.False(novo.Agent.Status.InheritedSecrets);
    }

    // ---- 7 a 9: a oferta única -------------------------------------------------------------------------------------

    [Fact]
    public async Task AlreadyConnected_DoesNotReceive_UntilYes()
    {
        var (casa, trabalho) = await TwoAsync();
        var touched = TouchedAccountCollections(trabalho);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        SaveCardAndDoc(casa);
        await Settle(casa, trabalho);

        Assert.False(HasAnyAccountGroup(trabalho));
        Assert.Null(trabalho.Profile.Secret(Collections.Cards, CardKey));
        Assert.Equal(touched, TouchedAccountCollections(trabalho)); // nenhum selo destas coleções foi aberto
        Assert.True(trabalho.Agent.Status.SecretsOffer);

        _asked.Clear();
        _answer = true;
        Assert.Null(await SyncChoices.AnswerOfferAsync(trabalho.Agent, accept: true, Confirm));
        Assert.Equal([ReauthPurpose.SyncSecrets], _asked);
        Assert.False(trabalho.Agent.Status.SecretsOffer);
        await Settle(casa, trabalho);
        Assert.NotNull(trabalho.Profile.Secret(Collections.Cards, CardKey));
        Assert.NotNull(trabalho.Profile.Secret(Collections.IdDocs, DocKey));
    }

    [Fact]
    public async Task Offer_No_IsNeverShownAgain_AndNothingArrives()
    {
        var (casa, trabalho) = await TwoAsync();
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        SaveCardAndDoc(casa);
        await Settle(casa, trabalho);
        Assert.True(trabalho.Agent.SecretsOffer);

        _asked.Clear();
        Assert.Null(await SyncChoices.AnswerOfferAsync(trabalho.Agent, accept: false, Confirm));
        Assert.Empty(_asked);
        Assert.False(trabalho.Agent.SecretsOffer);
        await Settle(casa, trabalho);
        Assert.False(trabalho.Agent.Status.SecretsOffer);
        Assert.False(Reopen(trabalho, "Trabalho").Agent.SecretsOffer);
        Assert.Null(trabalho.Profile.Secret(Collections.Cards, CardKey));
        Assert.False(HasAnyAccountGroup(trabalho));
    }

    [Fact]
    public async Task Offer_Yes_RefusedHello_StaysPending()
    {
        var (casa, trabalho) = await TwoAsync();
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, trabalho);

        _answer = false;
        Assert.Equal(Refused, await SyncChoices.AnswerOfferAsync(trabalho.Agent, accept: true, Confirm));
        Assert.True(trabalho.Agent.SecretsOffer);
        Assert.True(trabalho.Agent.Status.SecretsOffer);
        Assert.False(HasAnyAccountGroup(trabalho));
    }

    // ---- 10 e 11: desligar e voltar a entrar -----------------------------------------------------------------------

    [Theory]
    [InlineData("payments")]
    [InlineData("account")]
    public async Task TurningOffOnOnePc_KeepsTheAccountChoice_AndDeletesNothing(string row)
    {
        var casa = await CreateAsync();
        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.True(await casa.Agent.RunAsync());
        var asked = _asked.Count;

        Assert.Null(await SyncChoices.ChooseAsync(casa.Agent, row, on: false, Confirm));
        Assert.Equal(asked, _asked.Count); // desligar não pede nada
        Assert.DoesNotContain("cards", casa.Agent.EnabledCollections);
        await Settle(casa);
        Assert.True(casa.Agent.AccountSecrets);
        // O cartão continua na conta (e aqui): um computador novo que entra depois herda e recebe-o.
        Assert.NotNull(casa.Profile.Secret(Collections.Cards, CardKey));
        var novo = await JoinByKitAsync(casa, "Novo");
        Assert.True(HasAccountGroups(novo));
        Assert.NotNull(novo.Profile.Secret(Collections.Cards, CardKey));
        Assert.Equal(asked, _asked.Count);
    }

    [Fact]
    public async Task ReentryWithKeptKey_DoesNotInherit_GetsTheOffer()
    {
        var (casa, trabalho) = await TwoAsync();
        trabalho.Agent.SignOut(forgetKey: false);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        SaveCardAndDoc(casa);
        Assert.True(await casa.Agent.RunAsync());

        await trabalho.Agent.StartAsync();
        await Settle(casa, trabalho);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.False(HasAnyAccountGroup(trabalho));
        Assert.True(trabalho.Agent.Status.SecretsOffer);
        Assert.Null(trabalho.Profile.Secret(Collections.Cards, CardKey));
    }

    // ---- 12 a 17: o registo da conta -------------------------------------------------------------------------------

    [Fact]
    public async Task AccountRecord_IsNeverAppliedAsASetting_NorDeleted()
    {
        var proposed = new List<string>();
        var casa = await CreateAsync();
        var trabalho = await JoinByKitAsync(casa, "Trabalho");
        foreach (var device in new[] { casa, trabalho })
            device.Profile.Plain.Refuse = change =>
            {
                lock (proposed) proposed.Add(change.Collection + "/" + change.Key);
                return false;
            };
        casa.Profile.Plain.Setting("theme", "dark");
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, trabalho);
        await Settle(casa, trabalho);
        await Settle(casa, trabalho);

        Assert.DoesNotContain(proposed, p => p.Contains(Collections.AccountSecretsKey, StringComparison.Ordinal));
        Assert.Contains("settings/theme", proposed); // as definições normais continuam a passar
        Assert.True(casa.Agent.AccountSecrets);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.False(casa.Agent.HasLocalChanges());
        Assert.False(trabalho.Agent.HasLocalChanges());
        Assert.False(Collections.SyncableSettings.Contains(Collections.AccountSecretsKey));
        Assert.True(Collections.IsReservedSetting(Collections.AccountSecretsKey));
        Assert.DoesNotContain(Collections.SyncableSettings, Collections.IsReservedSetting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountRecord_UnknownValue_CountsAsOff(bool numeric)
    {
        var (casa, trabalho) = await TwoAsync();
        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, trabalho);
        Assert.True(trabalho.Agent.AccountSecrets);

        JsonNode value = numeric ? JsonValue.Create(1) : JsonValue.Create("talvez");
        await WriteAccountRecordAsync(casa, new JsonObject { ["value"] = value, ["since"] = _now, ["by"] = "x" });
        await Settle(casa, trabalho);
        Assert.False(casa.Agent.AccountSecrets);
        Assert.False(trabalho.Agent.AccountSecrets);

        var novo = await JoinByKitAsync(casa, "Novo");
        Assert.False(HasAnyAccountGroup(novo));
        Assert.Null(novo.Profile.Secret(Collections.Cards, CardKey));
    }

    [Fact]
    public async Task AccountRecord_LaterOff_Wins()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Agent.SetAccountSecrets(true);
        Assert.True(await casa.Agent.RunAsync());
        _now += 1000;
        trabalho.Agent.SetAccountSecrets(false);
        await Settle(casa, trabalho);
        Assert.False(casa.Agent.AccountSecrets);
        Assert.False(trabalho.Agent.AccountSecrets);
    }

    [Fact]
    public async Task AccountRecord_SurvivesANewChain()
    {
        var (casa, trabalho) = await TwoAsync();
        var terceiro = await JoinByKitAsync(casa, "Terceiro");
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        SaveCardAndDoc(casa);
        await Settle(casa, trabalho, terceiro);
        var devices = await casa.Agent.RefreshDevicesAsync();
        await trabalho.Agent.RefreshDevicesAsync();

        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Terceiro").Id);
        await Settle(casa, trabalho);
        Assert.True(casa.Agent.AccountSecrets);
        Assert.True(trabalho.Agent.AccountSecrets);
        var novo = await JoinByKitAsync(casa, "Novo");
        Assert.True(HasAccountGroups(novo));
        Assert.NotNull(novo.Profile.Secret(Collections.Cards, CardKey));
    }

    [Fact]
    public async Task DeviceList_SaysWhoReceivesPaymentsAndDocuments()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Agent.SetCollections(SyncGroups.AccountCollections, on: true);
        await Settle(casa, trabalho);
        // Um aparelho que é só um motor (sem agente): não diz que grupos tem, como um Éteris 0.2.14.
        using (var secret = AccountSecret.FromCode(casa.Agent.RevealKit()!.Words))
        {
            var old = await SyncEngine.JoinAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Antigo"), Now);
            await old.PullAsync();
        }

        var seenFromCasa = await casa.Agent.RefreshDevicesAsync();
        Assert.True(seenFromCasa.Single(d => d.Name == "Casa").PaymentsAndDocuments);
        Assert.False(seenFromCasa.Single(d => d.Name == "Trabalho").PaymentsAndDocuments);
        Assert.Null(seenFromCasa.Single(d => d.Name == "Antigo").PaymentsAndDocuments);
        var seenFromWork = await trabalho.Agent.RefreshDevicesAsync();
        Assert.True(seenFromWork.Single(d => d.Name == "Casa").PaymentsAndDocuments);
        Assert.False(seenFromWork.Single(d => d.Name == "Trabalho").PaymentsAndDocuments);
    }

    // Um Éteris 0.2.14 não tem o filtro das chaves reservadas: imita-se com uma chave de «settings» que nenhum perfil
    // conhece e que o filtro novo deixa passar. O perfil recusa-a (como BrowserProfile recusa o que não está em
    // SyncableSettings) e nada se estraga.
    [Fact]
    public async Task OlderEteris_IgnoresTheRecord()
    {
        using var secret = AccountSecret.Create();
        var writer = await SyncEngine.CreateAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Novo"), Now);
        writer.Put(Collections.Settings, "nao-conhecida", new JsonObject { ["value"] = "on", ["since"] = _now, ["by"] = writer.Device.Id });
        writer.Put(Collections.Settings, Collections.AccountSecretsKey, new JsonObject { ["value"] = "on", ["since"] = _now, ["by"] = writer.Device.Id });
        await writer.PushAsync();

        var reader = await SyncEngine.JoinAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Antigo"), Now);
        var profile = new MemoryProfile();
        var proposed = new List<string>();
        profile.Refuse = change =>
        {
            proposed.Add(change.Key);
            return change.Collection == Collections.Settings && !Collections.SyncableSettings.Contains(change.Key);
        };
        IReadOnlyDictionary<string, string>? baseline = null;
        for (var round = 0; round < 3; round++)
        {
            var (report, next) = await ProfileSync.RunAsync(reader, profile, baseline, "Antigo");
            await reader.PushAsync();
            baseline = next;
            Assert.Equal(0, report.Received);
            Assert.Equal(0, report.Sent);
        }
        Assert.Empty(profile.Items);
        Assert.Contains("nao-conhecida", proposed);
        Assert.DoesNotContain(Collections.AccountSecretsKey, proposed);
        await writer.PullAsync();
        Assert.Equal("on", writer.Get(Collections.Settings, "nao-conhecida")?.Text("value"));
        Assert.Equal("on", reader.Get(Collections.Settings, "nao-conhecida")?.Text("value"));
        Assert.False(reader.Get(Collections.Settings, Collections.AccountSecretsKey)!.Deleted);
        Assert.Equal(0, ProfileSync.CountLocalChanges(profile, baseline));
    }

    // ---- 18 a 20: HasSecrets reforçado (S24) ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_BeforeTheFirstRound_AsksWindows()
    {
        var casa = await CreateAsync();
        var novo = NewDevice("Novo");
        novo.Agent.SetCollections(Collections.Secret, on: false);
        novo = await JoinByKitAsync(casa, "Novo", novo);
        // O estado guardado de um aparelho que entrou e ainda não acabou uma ronda (ou de um Éteris anterior).
        var state = novo.Store.Load()!;
        state.SecretsInAccount = null;
        novo.Store.Save(state);
        var reopened = Reopen(novo, "Novo");
        Assert.True(reopened.Agent.HasSecrets);
        _answer = false;
        Assert.False(await SyncChoices.MayApproveAsync(reopened.Agent, Confirm));
        Assert.Equal([ReauthPurpose.ApproveDevice], _asked);
        // Depois de uma ronda completa sem segredos em lado nenhum, deixa de pedir.
        Assert.True(await reopened.Agent.RunAsync());
        Assert.False(reopened.Agent.HasSecrets);
    }

    [Fact]
    public async Task Approve_WithAccountGroupsOn_AndNoRecords_AsksWindows()
    {
        var casa = await CreateAsync();
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.True(await casa.Agent.RunAsync());
        var trabalho = NewDevice("Trabalho");
        trabalho.Agent.SetCollections(Collections.Secret, on: false);
        trabalho = await JoinByKitAsync(casa, "Trabalho", trabalho);
        Assert.True(await trabalho.Agent.RunAsync());
        // A conta não tem nenhum registo secreto, mas a escolha está ligada (e «Trabalho» herdou-a).
        trabalho.Agent.SetCollections(Collections.Secret, on: false);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.True(trabalho.Agent.HasSecrets);
        _asked.Clear();
        _answer = false;
        Assert.False(await SyncChoices.MayApproveAsync(trabalho.Agent, Confirm));
        Assert.Equal([ReauthPurpose.ApproveDevice], _asked);
    }

    [Fact]
    public async Task Approve_NoSecretsAnywhere_AfterAFullRound_DoesNotAsk()
    {
        var casa = await CreateAsync();
        var trabalho = NewDevice("Trabalho");
        trabalho.Agent.SetCollections(Collections.Secret, on: false);
        trabalho = await JoinByKitAsync(casa, "Trabalho", trabalho);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.False(trabalho.Agent.HasSecrets);
        Assert.True(await SyncChoices.MayApproveAsync(trabalho.Agent, Confirm));
        var reopened = Reopen(trabalho, "Trabalho");
        Assert.False(reopened.Agent.HasSecrets);
        Assert.True(await SyncChoices.MayApproveAsync(reopened.Agent, Confirm));
        Assert.Empty(_asked);
    }

    // ---- 21 a 23 ---------------------------------------------------------------------------------------------------

    private static bool HasControlOrFormat(string? text)
        => text is not null && text.Any(c => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format);

    [Fact]
    public async Task AccountChoice_SaysWhoTurnedItOn()
    {
        var (casa, trabalho) = await TwoAsync();
        var when = _now;
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, trabalho);
        _now += 60_000;
        await Settle(casa, trabalho);
        Assert.Equal("Casa", trabalho.Agent.AccountSecretsBy);
        Assert.Equal(when, trabalho.Agent.AccountSecretsSinceMs);
        Assert.Equal("Casa", casa.Agent.AccountSecretsBy);
        Assert.Equal(when, casa.Agent.AccountSecretsSinceMs);

        // Um «by» de um aparelho que ninguém conhece: não se sabe quem.
        await WriteAccountRecordAsync(casa, new JsonObject { ["value"] = "on", ["since"] = _now, ["by"] = "desconhecido" });
        await Settle(casa, trabalho);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.Null(trabalho.Agent.AccountSecretsBy);
    }

    // S21: o nome de quem ligou é um nome recebido (escolhido pelo outro aparelho): limpo antes de ser mostrado.
    [Fact]
    public async Task AccountChoice_SaysWhoTurnedItOn_HostileName()
    {
        var raw = "Portátil" + (char)0x202E + "gpj.exe\r\nSegunda linha";
        var casa = NewDevice(raw);
        await casa.Agent.StartAsync();
        var trabalho = await JoinByKitAsync(casa, "Trabalho");
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        await Settle(casa, trabalho);

        var by = trabalho.Agent.AccountSecretsBy;
        Assert.NotNull(by);
        Assert.False(HasControlOrFormat(by), by);
        Assert.Equal(DeviceNameText.Shown(raw), by);
        Assert.False(HasControlOrFormat(SyncChoices.AccountSince(by, trabalho.Agent.AccountSecretsSinceMs)));
        Assert.False(HasControlOrFormat(casa.Agent.AccountSecretsBy));
    }

    [Fact]
    public async Task Entry_AndInheritance_Flags()
    {
        var casa = await CreateAsync();
        Assert.Equal(SyncEntry.Created, casa.Agent.Status.Entry);
        Assert.False(casa.Agent.Status.InheritedSecrets);
        var antes = await JoinByKitAsync(casa, "Antes");
        Assert.Equal(SyncEntry.Joined, antes.Agent.Status.Entry);
        Assert.False(antes.Agent.Status.InheritedSecrets);
        Assert.DoesNotContain(SyncAgent.InheritedText, antes.Agent.Status.Notice ?? "");

        SaveCardAndDoc(casa);
        Assert.Null(await SyncChoices.ChooseAccountAsync(casa.Agent, Confirm));
        Assert.True(await casa.Agent.RunAsync());
        var novo = await JoinByApprovalAsync(casa, "Novo");
        var status = novo.Agent.Status;
        Assert.Equal(SyncEntry.Joined, status.Entry);
        Assert.True(status.InheritedSecrets);
        Assert.True(status.SecretsHere);
        Assert.True(status.AccountSecrets);
        Assert.EndsWith("Recebeu favoritos, palavras-passe, cartões e documentos.", status.Notice);
        Assert.Equal("Recebeu favoritos, palavras-passe, cartões e documentos", SyncAgent.InheritedText);
        // Uma ronda depois o aviso não se repete.
        Assert.True(await novo.Agent.RunAsync());
        Assert.Equal(2, novo.Agent.Status.Notice!.Split(SyncAgent.InheritedText).Length);

        var reopened = Reopen(novo, "Novo");
        Assert.Equal(SyncEntry.None, reopened.Agent.Status.Entry);
        Assert.False(reopened.Agent.Status.InheritedSecrets);
        Assert.True(reopened.Agent.Status.SecretsHere);
    }

    [Fact]
    public async Task PendingChoice_IsWrittenOnTheNextRound_EvenAfterRestart()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Agent.SetAccountSecrets(true);
        Assert.True(casa.Agent.HasLocalChanges());
        var reopened = Reopen(casa, "Casa");
        Assert.True(reopened.Agent.HasLocalChanges());
        Assert.True(reopened.Agent.AccountSecrets);
        Assert.True(await reopened.Agent.RunAsync());
        Assert.False(reopened.Agent.HasLocalChanges());
        await Settle(reopened, trabalho);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.True(reopened.Agent.AccountSecrets);
    }

    // ---- 32: quem já teve estado de sincronização não herda (R19) ------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedJoin_DoesNotInherit_GetsTheOffer(bool byKit)
    {
        var casa = await CreateAsync();
        var trabalho = await JoinByKitAsync(casa, "Trabalho");
        // O cartão só no cofre de «Trabalho» (pagamentos e documentos desligados aqui).
        trabalho.Profile.SetSecret(Collections.Cards, "c-trabalho", new JsonObject { ["number"] = "5555555555554444", ["expMonth"] = 2, ["expYear"] = 2030 });
        await Settle(casa, trabalho);

        // A cadeia é apagada; outro computador cria a conta nova e liga a escolha.
        await casa.Agent.DeleteCloudDataAsync();
        var outro = await CreateAsync("Outro");
        Assert.Null(await SyncChoices.ChooseAccountAsync(outro.Agent, Confirm));
        Assert.True(await outro.Agent.RunAsync());

        // «Trabalho» vê a conta substituída e entra na nova («Foi eu»: aprovação; ou com o kit novo).
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.True(trabalho.Agent.Status.ChainReplaced);
        var reads = trabalho.Profile.SecretReads;
        var touched = TouchedAccountCollections(trabalho);
        _asked.Clear();
        if (byKit) await JoinByKitAsync(outro, "Trabalho", trabalho);
        else
        {
            if (trabalho.Agent.Status.Phase == SyncPhase.Stopped) await trabalho.Agent.StartAsync();
            await JoinByApprovalAsync(outro, "Trabalho", trabalho);
        }
        await Settle(outro, trabalho);

        Assert.False(HasAnyAccountGroup(trabalho));
        Assert.Equal(reads, trabalho.Profile.SecretReads);
        Assert.Equal(touched, TouchedAccountCollections(trabalho));
        Assert.False(trabalho.Agent.Status.InheritedSecrets);
        Assert.True(trabalho.Agent.Status.SecretsOffer);
        Assert.DoesNotContain(ReauthPurpose.SyncSecrets, _asked);
        await Settle(outro, trabalho);
        Assert.Null(outro.Profile.Secret(Collections.Cards, "c-trabalho"));

        // Controlo: um computador novo que entra a seguir herda.
        var quarto = await JoinByApprovalAsync(outro, "Quarto");
        Assert.True(HasAccountGroups(quarto));
        Assert.False(quarto.Agent.Status.SecretsOffer);
        Assert.True(quarto.Agent.Status.InheritedSecrets);
    }

    // ---- revisão, ronda 1 ----------------------------------------------------------------------------------------

    // «since» é um valor recebido: fora do intervalo de datas (microssegundos, long.MaxValue) ou no futuro conta como
    // desconhecido (0), e as Definições continuam a abrir em todos os computadores da conta.
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1_762_560_000_000_000)]
    [InlineData(-5)]
    [InlineData(253_402_300_800_000)]
    public async Task AccountRecord_SinceOutOfRange_CountsAsUnknown(long since)
    {
        var (casa, trabalho) = await TwoAsync();
        await WriteAccountRecordAsync(casa, new JsonObject { ["value"] = "on", ["since"] = since, ["by"] = "x" });
        await Settle(casa, trabalho);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.Equal(0, trabalho.Agent.AccountSecretsSinceMs);
        Assert.Equal(0, casa.Agent.AccountSecretsSinceMs);
        var enabled = SyncGroups.DefaultCollections.Concat(SyncGroups.AccountCollections).ToHashSet(StringComparer.Ordinal);
        var row = SyncChoices.Rows(enabled, SyncChoices.AccountChoice.Of(trabalho.Agent)).Single(r => r.Id == SyncChoices.AccountRow);
        Assert.Equal(SyncChoices.AccountText, row.Text);
    }

    [Fact]
    public async Task AccountRecord_SinceInTheFuture_CountsAsUnknown()
    {
        var (casa, trabalho) = await TwoAsync();
        await WriteAccountRecordAsync(casa, new JsonObject { ["value"] = "on", ["since"] = _now + 2 * 86_400_000L, ["by"] = "x" });
        await Settle(casa, trabalho);
        Assert.True(trabalho.Agent.AccountSecrets);
        Assert.Equal(0, trabalho.Agent.AccountSecretsSinceMs);
    }
}
