using System.Globalization;
using System.IO;
using System.Text;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// O nome com que cada computador aparece aos outros: valor inicial, mudança, limpeza e o que chega de fora.
public sealed class DeviceNameTests : IDisposable
{
    private const string Hostile = "Portátil‮gpj.exe\r\nSegunda linha";

    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-device-name-" + Guid.NewGuid().ToString("N"));

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, Func<ISyncTransport>? transport = null, MemoryStore? store = null)
    {
        var profile = new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => new FolderTransport(_folder)), profile, store, name, Now, Quick)
            { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Device(agent, profile, store);
    }

    private Func<ISyncTransport> DriveFor(FakeGoogle google)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(_folder, "tokens-" + Guid.NewGuid().ToString("N")), new PlainProtector()), Now);
        session.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        return () => new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "À espera de: " + what);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    private async Task<(Device Casa, Device Trabalho)> TwoAsync(FakeGoogle google, string second = "Trabalho")
    {
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        var trabalho = NewDevice(second, DriveFor(google));
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho);
        return (casa, trabalho);
    }

    private static bool HasControlOrFormat(string text)
        => text.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format);

    private static void AssertNowhereInTheClear(FakeGoogle google, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var (name, data) in google.Files)
        {
            Assert.DoesNotContain(text, name, StringComparison.Ordinal);
            Assert.True(data.AsSpan().IndexOf(bytes) < 0, "O nome apareceu em claro em " + name);
        }
    }

    // ---- lógica pura ------------------------------------------------------------------------------------------------

    [Fact]
    public void Default_IsEterisEmTheWindowsName()
    {
        Assert.Equal("Éteris em PORTATIL", DeviceNameText.Default("PORTATIL"));
        Assert.Equal("Éteris", DeviceNameText.Default(""));
        Assert.Equal("Éteris", DeviceNameText.Default(null));
    }

    [Fact]
    public void Clean_RemovesControlAndFormatCharacters()
    {
        Assert.Equal("Casa de praia", DeviceNameText.Clean("Casa\n\t\0 de‮ pra​ia"));
        // Formato fora do plano básico (etiquetas U+E0000–E007F, invisíveis) e substitutos soltos também saem.
        Assert.Equal("Casa", DeviceNameText.Clean("Ca\U000E0041s\U000E007Fa"));
        Assert.Equal("Casa", DeviceNameText.Clean("Ca\uD800s\uDC00a"));
        Assert.Equal("Casa 😀", DeviceNameText.Clean("Casa 😀"));
    }

    [Fact]
    public void Clean_TrimsAndCollapsesSpaces()
    {
        Assert.Equal("Casa da praia", DeviceNameText.Clean("   Casa    da    praia  "));
    }

    [Fact]
    public void Clean_CutsToSixty_WithoutBreakingAPair()
    {
        Assert.Equal(60, DeviceNameText.Clean(new string('a', 80))!.Length);
        var withEmoji = new string('a', 59) + "😀" + "bbb";
        var cut = DeviceNameText.Clean(withEmoji)!;
        Assert.Equal(new string('a', 59), cut);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        var fits = DeviceNameText.Clean(new string('a', 58) + "😀" + "bbb")!;
        Assert.Equal(60, fits.Length);
        Assert.EndsWith("😀", fits, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002")]
    [InlineData(" \n\t‮ ")]
    public void Clean_NothingLeft_IsNull(string? typed) => Assert.Null(DeviceNameText.Clean(typed));

    [Fact]
    public void DeviceNameFor_KeepsTheTestProfileSuffix()
    {
        Assert.Equal("Éteris em PC", SyncService.DeviceNameFor("PC", null));
        Assert.Equal("Éteris em PC (perfil-a)", SyncService.DeviceNameFor("PC", "perfil-a"));
        Assert.NotEqual(SyncService.DeviceNameFor("PC", "perfil-a"), SyncService.DeviceNameFor("PC", "perfil-b"));
        Assert.Equal("Éteris (perfil-a)", SyncService.DeviceNameFor("", "perfil-a"));
        var long80 = new string('x', 80);
        Assert.True(SyncService.DeviceNameFor(long80, null).Length <= 60);
        Assert.True(SyncService.DeviceNameFor(long80, "perfil-a").Length <= 60);
    }

    [Fact]
    public void Shown_CleansWhatArrives_AndNamesTheEmpty()
    {
        var shown = DeviceNameText.Shown(Hostile);
        Assert.DoesNotContain('‮', shown);
        Assert.DoesNotContain('\r', shown);
        Assert.DoesNotContain('\n', shown);
        Assert.StartsWith("Portátil", shown, StringComparison.Ordinal);
        foreach (var empty in new[] { null, "", "  ", "\u0001" })
            Assert.Equal("Aparelho sem nome", DeviceNameText.Shown(empty));
        Assert.Equal(DeviceNameText.Unnamed, DeviceNameText.Shown(null));
        Assert.Equal(60, DeviceNameText.Shown(new string('b', 80)).Length);
    }

    // ---- agente -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_IsSavedOnThisComputer_AndSurvivesARestart()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        await casa.Agent.RenameDeviceAsync("Portátil da sala");
        Assert.Equal("Portátil da sala", casa.Agent.DeviceName);
        var reopened = NewDevice("Éteris em X", store: casa.Store);
        Assert.Equal("Portátil da sala", reopened.Agent.DeviceName);
    }

    [Fact]
    public async Task Rename_ReachesTheOtherComputers_Encrypted()
    {
        var google = new FakeGoogle();
        var (casa, trabalho) = await TwoAsync(google);
        await trabalho.Agent.RenameDeviceAsync("Portátil da sala");
        await Settle(trabalho, casa);
        await casa.Agent.RefreshDevicesAsync();
        Assert.Contains(casa.Agent.Status.Devices, d => d.Name == "Portátil da sala" && !d.IsThis);
        AssertNowhereInTheClear(google, "Portátil da sala");
    }

    // Mudar o nome logo ao abrir, antes da primeira ronda (sem motor carregado): a ronda seguinte leva o nome novo.
    [Fact]
    public async Task Rename_BeforeTheFirstRound_ReachesTheOtherComputers()
    {
        var google = new FakeGoogle();
        var (casa, trabalho) = await TwoAsync(google);
        var reopened = NewDevice("Éteris em X", DriveFor(google), trabalho.Store);
        await reopened.Agent.RenameDeviceAsync("Escritório novo");
        await Settle(reopened, casa);
        await casa.Agent.RefreshDevicesAsync();
        Assert.Contains(casa.Agent.Status.Devices, d => d.Name == "Escritório novo" && !d.IsThis);
        Assert.DoesNotContain(casa.Agent.Status.Devices, d => d.Name == "Trabalho");
        Assert.Equal("Escritório novo", trabalho.Store.Load()!.Engine!.Device.Name);
        await reopened.Agent.RefreshDevicesAsync();
        Assert.Equal("Escritório novo", reopened.Agent.Status.Devices.Single(d => d.IsThis).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0001")]
    public async Task Rename_Empty_IsRefused(string typed)
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        await Assert.ThrowsAsync<SyncUserException>(() => casa.Agent.RenameDeviceAsync(typed));
        await Assert.ThrowsAsync<SyncUserException>(() => casa.Agent.RenameDeviceAsync(null));
        Assert.Equal("Casa", casa.Agent.DeviceName);
    }

    [Fact]
    public async Task Rename_IsCleaned()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        await casa.Agent.RenameDeviceAsync("  Casa\n\n ");
        Assert.Equal("Casa", casa.Agent.DeviceName);
        await casa.Agent.RenameDeviceAsync(new string('z', 80));
        Assert.Equal(new string('z', 60), casa.Agent.DeviceName);
        Assert.Equal(new string('z', 60), casa.Store.Load()!.Engine!.Device.Name);
    }

    [Fact]
    public async Task PairingRequest_ShowsTheNewName_AndNeverInTheClear()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        casa.Agent.AcknowledgeCode();
        var portatil = NewDevice("Portátil", DriveFor(google));
        await portatil.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, portatil.Agent.Status.Phase);
        await portatil.Agent.RenameDeviceAsync("Portátil da sala");

        portatil.Agent.RequestPairing();
        await Until(() => google.Files.Any(f => f.Name.EndsWith("/0-request", StringComparison.Ordinal)), "o pedido no Drive");
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
        Assert.Equal("Portátil da sala", casa.Agent.Status.Requests[0].Name);
        foreach (var (name, data) in google.Files.Where(f => f.Name.Contains("/pair/", StringComparison.Ordinal)))
            Assert.True(data.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Portátil da sala")) < 0, "em claro: " + name);
    }

    [Fact]
    public async Task AlreadyLinked_KeepsItsName()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        var reopened = NewDevice("Éteris em X", DriveFor(google), casa.Store);
        Assert.Equal("Casa", reopened.Agent.DeviceName);
        Assert.True(await reopened.Agent.RunAsync());
        await reopened.Agent.RefreshDevicesAsync();
        Assert.Equal("Casa", reopened.Agent.Status.Devices.Single(d => d.IsThis).Name);
    }

    [Fact]
    public async Task NewComputer_StartsAsEterisEm()
    {
        var casa = NewDevice(DeviceNameText.Default("PC"));
        await casa.Agent.StartAsync();
        await casa.Agent.RefreshDevicesAsync();
        Assert.Equal("Éteris em PC", casa.Agent.Status.Devices.Single(d => d.IsThis).Name);
        Assert.Equal("Éteris em PC", casa.Store.Load()!.Engine!.Device.Name);
    }

    [Fact]
    public async Task Rename_BeforeJoining_IsUsedWhenJoining()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, trabalho.Agent.Status.Phase);
        await trabalho.Agent.RenameDeviceAsync("Escritório");
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        Assert.Equal("Escritório", trabalho.Store.Load()!.Engine!.Device.Name);
    }

    [Fact]
    public async Task HostileName_IsCleanInTheDeviceList_AndInTheRequest()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        var hostil = NewDevice(Hostile, DriveFor(google));
        await hostil.Agent.StartAsync();
        await hostil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, hostil);
        await casa.Agent.RefreshDevicesAsync();
        Assert.All(casa.Agent.Status.Devices, d => Assert.False(HasControlOrFormat(d.Name), d.Name));
        Assert.Contains(casa.Agent.Status.Devices, d => d.Name == DeviceNameText.Shown(Hostile));

        casa.Agent.AcknowledgeCode();
        var terceiro = NewDevice(Hostile, DriveFor(google));
        await terceiro.Agent.StartAsync();
        terceiro.Agent.RequestPairing();
        await Until(() => google.Files.Any(f => f.Name.EndsWith("/0-request", StringComparison.Ordinal)), "o pedido no Drive");
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
        Assert.False(HasControlOrFormat(casa.Agent.Status.Requests[0].Name));
        Assert.Equal(DeviceNameText.Shown(Hostile), casa.Agent.Status.Requests[0].Name);
    }

    [Fact]
    public void Agent_ShowsReceivedNamesOnlyThroughDeviceNameText()
    {
        var src = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Sync", "Engine", "SyncAgent.cs"), Encoding.UTF8);
        Assert.DoesNotContain("\"Aparelho sem nome\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("«{flow.Name}»", src, StringComparison.Ordinal);
        Assert.DoesNotContain("«{from}»", src, StringComparison.Ordinal);
        Assert.DoesNotContain("«{rekey.FromName}»", src, StringComparison.Ordinal);
        var uses = src.Split("DeviceNameText.Shown(").Length - 1;
        Assert.True(uses >= 5, "DeviceNameText.Shown( só aparece " + uses + " vezes");
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
