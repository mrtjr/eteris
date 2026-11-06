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
        VelopackApp.Build().Run();

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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
