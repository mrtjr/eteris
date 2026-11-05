using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eteris.Browser.Infrastructure;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name: @"Local\" + name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void ActivateExisting(string processName)
    {
        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            if (process.Id == current || process.MainWindowHandle == 0) continue;
            SetForegroundWindow(process.MainWindowHandle);
            return;
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
