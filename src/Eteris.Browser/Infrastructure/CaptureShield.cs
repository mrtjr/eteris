using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Eteris.Browser.Infrastructure;

// «Esconder o Éteris do Windows Recall e de capturas» (Definições › Privacidade e selo «Privada»): com a afinidade de
// ecrã WDA_EXCLUDEFROMCAPTURE as janelas do Éteris ficam fora das capturas de ecrã, das gravações, da partilha de ecrã
// e das imagens do Recall. Desligado por omissão (também na janela privada): ligado, as capturas mostram um vazio.
// A afinidade só se aplica a janelas de topo; a página (janela filha do Chromium) fica coberta pela da janela principal,
// e as janelas de topo que o Chromium tenha abertas (listas de <select>) são apanhadas ao percorrer as do processo.
public static class CaptureShield
{
    public const uint WdaNone = 0x0;
    public const uint WdaMonitor = 0x1;
    public const uint WdaExcludeFromCapture = 0x11;

    // WDA_EXCLUDEFROMCAPTURE existe desde o Windows 10 2004 (19041); antes só WDA_MONITOR (a janela aparece preta).
    public static uint AffinityFor(bool hide, int build) => !hide ? WdaNone : build >= 19041 ? WdaExcludeFromCapture : WdaMonitor;

    public static uint Wanted => AffinityFor(Data.Settings.Current.HideFromCapture, Environment.OSVersion.Version.Build);

    private static bool _installed;

    // Cada janela WPF que abre segue a definição; mudar a definição aplica-a a todas as janelas do processo.
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) Apply(new WindowInteropHelper(window).Handle);
        }));
        var last = Data.Settings.Current.HideFromCapture;
        Data.Settings.Changed += settings =>
        {
            if (settings.HideFromCapture == last) return;
            last = settings.HideFromCapture;
            Application.Current?.Dispatcher.InvokeAsync(ApplyAll);
        };
        if (last) ApplyAll();
    }

    public static bool Apply(nint hwnd) => hwnd != 0 && SetWindowDisplayAffinity(hwnd, Wanted);

    public static void ApplyAll()
    {
        foreach (var hwnd in ProcessWindows()) Apply(hwnd);
    }

    // O estado real de uma janela (eteris://privacy/ mostra-o): a afinidade que o Windows tem registada.
    public static bool IsExcluded(nint hwnd)
        => hwnd != 0 && GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != WdaNone;

    // Quantas janelas visíveis do Éteris há e quantas estão escondidas das capturas (para eteris://privacy/).
    public static (int Visible, int Hidden) Count()
    {
        var visible = ProcessWindows().Where(IsWindowVisible).ToList();
        return (visible.Count, visible.Count(IsExcluded));
    }

    private static List<nint> ProcessWindows()
    {
        var pid = (uint)Environment.ProcessId;
        var found = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) found.Add(hwnd);
            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

// Textos do selo «Privada» e do aviso (testados: dizem sempre o preço de ligar — as capturas ficam vazias).
public static class CaptureText
{
    public static string BadgeTip(bool hidden) => hidden
        ? "Janela privada · escondida do Recall e das capturas de ecrã. Clique para voltar a mostrar."
        : "Janela privada: nada fica no histórico, nos cookies nem na cache. Clique para esconder o Éteris do Recall e das capturas de ecrã.";

    public static string Toast(bool hidden) => hidden
        ? "Éteris escondido do Recall e das capturas: capturas, gravações e partilhas de ecrã mostram um vazio"
        : "O Éteris volta a aparecer nas capturas de ecrã e no Recall";
}
