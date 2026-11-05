using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

public static class Program
{
    private const string InstanceName = "Eteris.Browser";

    [STAThread]
    public static void Main()
    {
        using var instance = SingleInstance.TryAcquire(InstanceName);
        if (instance is null)
        {
            SingleInstance.ActivateExisting(InstanceName);
            return;
        }

        new App().Run();
    }
}
