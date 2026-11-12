using Eteris.Browser.Infrastructure;
using Eteris.Browser.Updates;
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
        // Rede de segurança em dois degraus (D-18): um arranque anterior desta versão falhou com ajustes de desempenho antes
        // da 1.ª página: este corre sem nenhum (o sandbox só cai à 2.ª). Antes da 1.ª leitura de um ajuste, a do e11.
        if (StartupGuard.SuspendTweaksAfterAFailedStart(args)) StartupTrace.Mark("sem.ajustes");
        // Depois de instalar e de atualizar: a ACL da pasta para o serviço de rede com sandbox (NetworkSandbox, D9); antes
        // de desinstalar: o perfil do AppContainer dele, que ficaria no utilizador.
        // O Velopack nunca aplica no arranque (INF-03): fazia-o aqui, antes da instância única, e o Update.exe terminava o
        // Éteris que estivesse aberto (um 2.º lançamento, um link noutra aplicação). O Éteris aplica ao sair
        // (UpdateService.ApplyPendingOnExit) ou, já instância principal, com UpdateService.ApplyPendingAtStartup.
        // Reaberto pelo Update.exe depois de aplicar: o Run apaga a VELOPACK_RESTART, por isso lê-se antes (I1, a guarda do
        // Velopack contra um ciclo de reinícios, com o mesmo teste que ele usa).
        var restartedByUpdater = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VELOPACK_RESTART"));
        var velopack = VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => NetworkSandbox.OnInstalled())
            .OnAfterUpdateFastCallback(_ => NetworkSandbox.OnInstalled())
            .OnBeforeUninstallFastCallback(_ => NetworkSandbox.OnUninstalling())
            .SetAutoApplyOnStartup(false);
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
        // Uma atualização já descarregada aplica-se agora só se este for o único Éteris da instalação e não trouxer link nem
        // pedido de janela privada, nem foi reaberto pelo Update.exe nem é a nova tentativa do sandbox; senão fica para a
        // saída (INF-03).
        UpdateService.ApplyPendingAtStartup(args, restartedByUpdater);
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
