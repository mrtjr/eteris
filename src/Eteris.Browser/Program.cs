using Eteris.Browser.Infrastructure;
using Velopack;

namespace Eteris.Browser;

public static class Program
{
    private const string InstanceName = "Eteris.Browser";

    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        using var instance = SingleInstance.TryAcquire(InstanceName);
        if (instance is null)
        {
            SingleInstance.ActivateExisting(InstanceName);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
