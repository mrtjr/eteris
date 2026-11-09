using Eteris.Browser.Infrastructure;
using Velopack;

namespace Eteris.Browser;

public static class Program
{
    private const string InstanceName = "Eteris.Browser";

    // Nome do canal desta instância (por perfil), para receber links de lançamentos posteriores.
    public static string ChannelName { get; private set; } = InstanceName;

    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Mark("main");
        // Depois de instalar e de atualizar: a ACL da pasta para o serviço de rede com sandbox (NetworkSandbox, D9); antes
        // de desinstalar: o perfil do AppContainer dele, que ficaria no utilizador.
        var velopack = VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => NetworkSandbox.OnInstalled())
            .OnAfterUpdateFastCallback(_ => NetworkSandbox.OnInstalled())
            .OnBeforeUninstallFastCallback(_ => NetworkSandbox.OnUninstalling());
        // Ajuste e10 (plano de desempenho da 0.2.14, à espera da decisão D-7; desligado por omissão): o Éteris já aplica as
        // atualizações ao sair (UpdateService.ApplyPendingOnExit); sem isto o Velopack procura também, em cada arranque,
        // um pacote descarregado para aplicar. Uma atualização descarregada antes de uma saída que falhou espera pela
        // próxima saída limpa (o UpdateService volta a encontrá-la depois da 1.ª página).
        if (PerfTuning.IsOn(PerfTuning.UpdatesOnExitOnly)) velopack = velopack.SetAutoApplyOnStartup(false);
        velopack.Run();
        StartupTrace.Mark("velopack");

        if (StartupArgs.UserDataDir(args) is { } userDataDir) AppPaths.UseRoot(userDataDir);
        var instanceName = AppPaths.IsCustomProfile
            ? InstanceName + "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..12]
            : InstanceName;

        ChannelName = instanceName;
        using var instance = SingleInstance.TryAcquire(instanceName);
        if (instance is null)
        {
            // Já há um Éteris aberto neste perfil: entrega-lhe os argumentos (um link, --private) e sai.
            // Quem envia tem direito ao primeiro plano; passa-o à instância aberta para a janela poder vir à frente.
            SingleInstance.AllowAnyForeground();
            if (InstanceChannel.Send(instanceName, args)) return;
            if (!AppPaths.IsCustomProfile) SingleInstance.ActivateExisting(InstanceName);
            return;
        }
        StartupTrace.Mark("instancia");
        // O JIT do arranque passa a correr também noutros núcleos, guiado pelo arranque anterior (Infrastructure/StartupJit.cs);
        // um arranque privado não o grava.
        if (StartupJit.ShouldRecord(args)) StartupJit.Start(AppPaths.Root);
        // Ajuste e11 (desligado por omissão; regra R-2 e depois do E9): o motor prepara-se e arranca já, na sua thread,
        // enquanto os recursos da aplicação e a janela se carregam aqui. As pastas antes de tudo: a base de dados
        // (App.OnStartup, PrewarmAsync) já não espera pelo Prepare.
        if (PerfTuning.IsOn(PerfTuning.EngineThreadStartup))
        {
            AppPaths.EnsureDirectories();
            CefConfigurator.StartFromMain(App.OnCefReady);
            StartupTrace.Mark("motor.lancado");
        }
        // O motor de composição do WPF arranca já, numa thread à parte (Infrastructure/CompositionWarmup.cs).
        CompositionWarmup.Start();

        var app = new App();
        app.InitializeComponent();
        StartupTrace.Mark("app.recursos");
        app.Run();
    }
}
