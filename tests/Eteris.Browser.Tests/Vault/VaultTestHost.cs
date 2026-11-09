using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// O que as páginas do cofre pedem ao processo do navegador, a fingir: diálogos de ficheiro, área de transferência,
// capturas, abas e definições. Regista tudo para os testes verem o que foi pedido.
internal sealed class FakeVaultHost : IVaultHost
{
    public nint Window = 0x4242;
    public string? ImportPath;
    public string? ExportPath;
    public bool ConfirmAnswer = true;
    public readonly List<string> Confirms = [];
    public readonly List<string> Copied = [];
    public readonly List<string> Recycled = [];
    public readonly List<string> Opened = [];
    public readonly List<(TimeSpan Delay, Action Run)> Scheduled = [];
    public int ImportDialogs, ExportDialogs, Shown, Hidden;
    public bool Synced;
    public bool SyncsPasswords => Synced;
    public UserSettings Current = new();
    public CancellationTokenSource Front = new();

    public Task<nint> WindowAsync(int browserId) => Task.FromResult(Window);
    public CancellationToken WhileInFront(int browserId) => Front.Token;

    public Task<string?> PickImportFileAsync(int browserId)
    {
        ImportDialogs++;
        return Task.FromResult(ImportPath);
    }

    public Task<string?> PickExportFileAsync(int browserId, string suggestedName)
    {
        ExportDialogs++;
        return Task.FromResult(ExportPath);
    }

    public Task<bool> ConfirmAsync(int browserId, string title, string message)
    {
        Confirms.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public bool Recycle(string path)
    {
        Recycled.Add(path);
        File.Delete(path);
        return true;
    }

    public bool CopySecret(string text)
    {
        Copied.Add(text);
        return true;
    }

    public IDisposable ShowSecret()
    {
        Shown++;
        return new Release(() => Hidden++);
    }

    public void OpenTab(int browserId, string url) => Opened.Add(url);

    public IReadOnlyDictionary<string, string> Icons(IEnumerable<string> urls) => new Dictionary<string, string>();

    public UserSettings Settings => Current;

    public bool SetSetting(string key, string value)
    {
        if (Current.With(key, value) is not { } next) return false;
        Current = next;
        return true;
    }

    public IDisposable Schedule(TimeSpan delay, Action run)
    {
        Scheduled.Add((delay, run));
        return new Release(() => { });
    }

    public void RunScheduled()
    {
        var all = Scheduled.ToList();
        Scheduled.Clear();
        foreach (var (_, run) in all) run();
    }

    private sealed class Release(Action action) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            action();
        }
    }
}

internal sealed class FakeReauth : IReauth
{
    public int Prompts;
    public ReauthOutcome Outcome = ReauthOutcome.Verified;
    public readonly List<string> Messages = [];

    public ReauthAvailability CheckAvailability() => ReauthAvailability.Hello;

    public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
    {
        Prompts++;
        Messages.Add(message);
        return Task.FromResult(cancel.IsCancellationRequested ? ReauthOutcome.Canceled : Outcome);
    }
}

// Um cofre num diretório temporário, com as rotas das páginas e os falsos.
internal sealed class VaultFixture : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "eteris-vault-pages-" + Guid.NewGuid().ToString("N"));
    public DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    public readonly VaultService Vault;
    public readonly FakeReauth Reauth = new();
    public readonly ReauthGate Gate;
    public readonly FakeVaultHost Host = new();
    public readonly VaultRoutes Routes;

    public VaultFixture()
    {
        Directory.CreateDirectory(Root);
        Vault = new VaultService(Root, new Dpapi(VaultKey.Purpose), () => Now);
        Gate = new ReauthGate(Reauth, () => Now);
        Routes = new VaultRoutes(Vault, Gate, Host);
    }

    public Task<VaultReply> Get(string host, string path, string query = "", bool authorized = true, bool isPrivate = false)
        => Routes.HandleAsync(new VaultRequest(host, path, Parse(query), new Dictionary<string, string>(), authorized, isPrivate, 7));

    public Task<VaultReply> Post(string host, string path, Dictionary<string, string> form, string query = "", bool authorized = true)
        => Routes.HandleAsync(new VaultRequest(host, path, Parse(query), form, authorized, false, 7));

    public static System.Text.Json.Nodes.JsonObject Json(VaultReply reply) => System.Text.Json.Nodes.JsonNode.Parse(reply.Body)!.AsObject();

    private static Dictionary<string, string> Parse(string query)
        => query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);

    public void Dispose()
    {
        Vault.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
