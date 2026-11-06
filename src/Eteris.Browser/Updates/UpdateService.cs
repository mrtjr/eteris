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
            var manager = new UpdateManager(source, null, locator);
            if (!manager.IsInstalled) return null;
            return await manager.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
        }
    }

    public static async Task CheckAndDownloadAsync()
    {
        if (State == UpdateState.Ready || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            UpdateManager? manager = null;
            try { manager = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
            catch (Exception) { /* fora de uma instalação Velopack */ }
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
