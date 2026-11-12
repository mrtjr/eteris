using System.IO;
using System.Net.Http;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Eteris.Browser.Updates;

public enum UpdateState { Development, Checking, UpToDate, Ready, Failed }

public static class UpdateService
{
    private static UpdateState _state = UpdateState.Checking;

    public static UpdateState State
    {
        get => _state;
        private set
        {
            _state = value;
            StateChanged?.Invoke();
        }
    }

    public static event Action? StateChanged;

    // Pedido pela pílula «Atualizar»: ao sair, o Update.exe aplica a versão nova e reabre o Éteris.
    public static bool RestartRequested { get; set; }

    // Só numa versão de desenvolvimento: mostra a pílula sem haver atualização real (para testar o aspeto).
    public static void SimulateReadyForDemo(string version)
    {
        if (State is not UpdateState.Development) return;
        _demoVersion = version;
        State = UpdateState.Ready;
    }

    private static string? _demoVersion;


    public static string? ReadyVersion => Pending?.TargetFullRelease.Version.ToString() ?? _demoVersion;

    public static string Describe(UpdateState state, string? version) => state switch
    {
        UpdateState.Development => "Versão de desenvolvimento: as atualizações só funcionam na versão instalada.",
        UpdateState.Checking => "A procurar atualizações…",
        UpdateState.UpToDate => "O Éteris está atualizado.",
        UpdateState.Ready => $"A versão {version} está pronta: é instalada quando fechar o Éteris.",
        _ => "Não foi possível procurar atualizações (sem ligação?).",
    };

    public static string Status => Describe(State, ReadyVersion);

    private static int _checking;

    public const string RepoUrl = "https://github.com/mrtjr/eteris";

    private static UpdateManager? _manager;

    public static UpdateInfo? Pending { get; private set; }

    public static async Task<UpdateInfo?> CheckAsync(IUpdateSource source, IVelopackLocator? locator = null)
    {
        try
        {
            var checker = new UpdateManager(source, null, locator);
            if (!checker.IsInstalled) return null;
            return await checker.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
        }
    }

    // O único sítio onde nasce o UpdateManager do Éteris; nulo fora de uma instalação Velopack.
    private static UpdateManager? NewManager()
    {
        try { return new UpdateManager(CreateSource()); }
        catch (Exception) { return null; }
    }

    // A fonte das atualizações: as releases do GitHub, filtradas pelo índice assinado da última release (INF-02, CAD-01;
    // docs/atualizacoes.md). Sem chaves em update-keys.txt, nenhuma atualização é aceite.
    public static IUpdateSource CreateSource()
        => new SignedUpdateSource(new GithubSource(RepoUrl, null, false), FetchLatestAsset, UpdateSignature.EmbeddedKeys);

    // Tamanho máximo do índice e da assinatura: um índice com todas as versões tem poucos KB.
    private const int MaxAssetBytes = 1024 * 1024;

    // Criado só na 1.ª procura (nada de rede nem de objetos novos no arranque).
    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");
        return http;
    });

    // Um ficheiro da última release publicada (o índice ou a assinatura), no máximo MaxAssetBytes.
    private static async Task<byte[]> FetchLatestAsset(string name, CancellationToken cancel)
    {
        using var response = await s_http.Value.GetAsync(RepoUrl + "/releases/latest/download/" + Uri.EscapeDataString(name),
            HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxAssetBytes) throw new HttpRequestException($"{name} grande demais");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxAssetBytes) throw new HttpRequestException($"{name} grande demais");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Aplicar no arranque um pacote já descarregado (INF-03): só se houver um, se nenhum outro processo correr a partir
    // desta instalação (outro perfil aberto, ou os filhos dele) e se o lançamento não trouxer um link nem uma janela
    // privada (o Update.exe escreve no seu registo os argumentos com que reabre o Éteris). Nunca quando o Éteris foi
    // reaberto pelo Update.exe (VELOPACK_RESTART, a guarda que o VelopackApp.Run tinha contra um ciclo de reinícios) nem na
    // nova tentativa do sandbox (SandboxFallback.RetrySwitch: a versão nova arrancaria sem sandbox sem razão).
    public static bool ShouldApplyAtStartup(bool pending, bool otherProcessFromInstall, bool carriesUrlOrPrivate,
        bool restartedByUpdater, bool sandboxRetry)
        => pending && !otherProcessFromInstall && !carriesUrlOrPrivate && !restartedByUpdater && !sandboxRetry;

    // Lançamento da DLL cliente do bootstrap depois de o .NET falhar com o sandbox (como no CefConfigurator).
    public static bool IsSandboxRetry(IEnumerable<string> args)
        => args.Any(a => a.Equals(Infrastructure.SandboxFallback.RetrySwitch, StringComparison.OrdinalIgnoreCase));

    // Chamado no Program.Main logo a seguir à instância única, com o VELOPACK_RESTART lido antes do VelopackApp.Run (que o
    // apaga). Nunca lança: uma falha deixa o pacote para a saída.
    public static void ApplyPendingAtStartup(string[] args, bool restartedByUpdater)
    {
        try
        {
            if (NewManager() is not { IsInstalled: true } manager || manager.UpdatePendingRestart is not { } asset) return;
            var carries = Infrastructure.StartupArgs.InitialUrl(args) is not null || Infrastructure.StartupArgs.IsPrivate(args);
            if (!ShouldApplyAtStartup(true, OtherProcessFromInstall(), carries, restartedByUpdater, IsSandboxRetry(args))) return;
            manager.ApplyUpdatesAndRestart(asset, args);
        }
        catch
        {
            // fica para a saída
        }
    }

    // Outro processo com o nome deste e o mesmo executável (um processo que não se deixa ler conta como outro).
    private static bool OtherProcessFromInstall()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = Environment.ProcessPath;
        var processes = System.Diagnostics.Process.GetProcessesByName(self.ProcessName);
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == self.Id) continue;
                string? file;
                try { file = process.MainModule?.FileName; }
                catch { return true; }
                if (path is null || file is null || string.Equals(file, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public static async Task CheckAndDownloadAsync()
    {
        if (State == UpdateState.Ready || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var manager = NewManager();
            if (manager is null || !manager.IsInstalled)
            {
                State = UpdateState.Development;
                return;
            }
            State = UpdateState.Checking;
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                State = UpdateState.UpToDate;
                return;
            }
            await manager.DownloadUpdatesAsync(update);
            _manager = manager;
            Pending = update;
            State = UpdateState.Ready;
        }
        catch
        {
            // sem rede ou download interrompido: tenta no próximo arranque
            State = UpdateState.Failed;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    // O Velopack não aplica pacotes descarregados sozinho: Update.exe espera que o processo saia e troca a pasta current\.
    public static void ApplyPendingOnExit()
    {
        if (Pending is null || _manager is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: RestartRequested);
        }
        catch
        {
            // fica para o próximo arranque
        }
    }
}
