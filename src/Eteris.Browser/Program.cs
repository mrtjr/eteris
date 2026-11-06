using Eteris.Browser.Infrastructure;
using Velopack;

namespace Eteris.Browser;

public static class Program
{
    private const string InstanceName = "Eteris.Browser";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        if (StartupArgs.UserDataDir(args) is { } userDataDir) AppPaths.UseRoot(userDataDir);
        var instanceName = AppPaths.IsCustomProfile
            ? InstanceName + "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..12]
            : InstanceName;

        using var instance = SingleInstance.TryAcquire(instanceName);
        if (instance is null)
        {
            // Num perfil alternativo não se traz a janela do perfil normal para a frente.
            if (!AppPaths.IsCustomProfile) SingleInstance.ActivateExisting(InstanceName);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
