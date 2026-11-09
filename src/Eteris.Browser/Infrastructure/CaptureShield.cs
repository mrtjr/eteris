using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Eteris.Browser.Infrastructure;

// «Esconder o Éteris do Windows Recall e de capturas» (Definições › Privacidade e selo «Privada»): com a afinidade de
// ecrã WDA_EXCLUDEFROMCAPTURE as janelas do Éteris ficam fora das capturas de ecrã, das gravações, da partilha de ecrã
// e das imagens do Recall. Desligado por omissão (também na janela privada): ligado, as capturas mostram um vazio.
// A afinidade só se aplica a janelas de topo; a página (janela filha do Chromium) fica coberta pela da janela principal.
// Cada janela de topo do processo que se cria ou aparece depois — os Popups do WPF (sugestões da barra de endereço,
// cadeado, lista de abas, menus de contexto, dicas), as listas de <select> e o preenchimento automático do Chromium —
// é apanhada por um gancho de eventos do Windows (SetWinEventHook, só deste processo) e recebe a mesma afinidade.
public static class CaptureShield
{
    public const uint WdaNone = 0x0;
    public const uint WdaMonitor = 0x1;
    public const uint WdaExcludeFromCapture = 0x11;

    // WDA_EXCLUDEFROMCAPTURE existe desde o Windows 10 2004 (19041); antes só WDA_MONITOR (a janela aparece preta).
    public static uint AffinityFor(bool hide, int build) => !hide ? WdaNone : build >= 19041 ? WdaExcludeFromCapture : WdaMonitor;

    public static uint Wanted => AffinityFor(Data.Settings.Current.HideFromCapture, Environment.OSVersion.Version.Build);

    private static bool _installed;

    // Testes: a afinidade a usar sem ler as definições do perfil.
    internal static Func<uint>? WantedOverride;

    private static uint Current => WantedOverride?.Invoke() ?? Wanted;

    // O gancho fica vivo enquanto o processo existir (o delegado não pode ser recolhido).
    private static WinEventProc? s_hook;
    private static nint s_hookHandle;
    // A thread que instalou o gancho: os eventos só chegam por ela, e o Windows desfaz o gancho quando ela termina.
    private static Thread? s_hookThread;
    private static readonly object s_hookLock = new();

    // Cada janela WPF que abre segue a definição; mudar a definição aplica-a a todas as janelas do processo.
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) Apply(new WindowInteropHelper(window).Handle);
        }));
        HookNewWindows();
        var last = Data.Settings.Current.HideFromCapture;
        Data.Settings.Changed += settings =>
        {
            if (settings.HideFromCapture == last) return;
            last = settings.HideFromCapture;
            Application.Current?.Dispatcher.InvokeAsync(ApplyAll);
        };
        if (last) ApplyAll();
    }

    public static bool Apply(nint hwnd) => hwnd != 0 && SetWindowDisplayAffinity(hwnd, Current);

    // Uma janela de topo deste processo criada ou mostrada (EVENT_OBJECT_CREATE/SHOW): com a definição ligada, fica já
    // fora das capturas. O evento chega pela fila de mensagens da thread que instalou o gancho (fora de contexto).
    // Em produção corre uma vez, na thread da interface (vive tanto como o processo). Chamado de outra thread (os testes),
    // volta a instalar: o gancho de uma thread que já terminou deixou de existir (UnhookWinEvent automático do Windows).
    // Se a primeira thread ainda viver, ficam dois ganchos ativos durante algum tempo; não faz mal, porque a afinidade
    // aplicada é a mesma (idempotente).
    public static void HookNewWindows()
    {
        lock (s_hookLock)
        {
            if (s_hookHandle != 0 && s_hookThread == Thread.CurrentThread) return;
            s_hook = OnWinEvent;
            s_hookHandle = SetWinEventHook(EventObjectCreate, EventObjectShow, 0, s_hook, (uint)Environment.ProcessId, 0, WinEventOutOfContext);
            s_hookThread = Thread.CurrentThread;
        }
    }

    // Testes: desfaz o gancho. O UnhookWinEvent só vale na thread que o instalou; noutra, só se esquece o identificador
    // (o próximo HookNewWindows instala outro).
    internal static void UnhookForTests()
    {
        lock (s_hookLock)
        {
            if (s_hookHandle != 0 && s_hookThread == Thread.CurrentThread) UnhookWinEvent(s_hookHandle);
            s_hookHandle = 0;
            s_hookThread = null;
        }
    }

    private static void OnWinEvent(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || idObject != ObjIdWindow || @event == EventObjectDestroy) return;
        try
        {
            var wanted = Current;
            if (wanted == WdaNone || GetAncestor(hwnd, GaRoot) != hwnd) return;
            if (!GetWindowDisplayAffinity(hwnd, out var affinity) || affinity != wanted) SetWindowDisplayAffinity(hwnd, wanted);
        }
        catch (Exception) { }
    }

    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;

    private delegate void WinEventProc(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

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
