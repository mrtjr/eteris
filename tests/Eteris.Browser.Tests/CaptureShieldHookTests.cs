using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O gancho do «Esconder do Recall e de capturas» (SetWinEventHook fora de contexto) só entrega eventos na thread que o
// instalou, e o Windows desfaz-o quando essa thread termina. Antes, uma segunda chamada a HookNewWindows() vinda de outra
// thread (repetir o teste, outro teste) ficava sem gancho, porque o primeiro identificador nunca voltava a 0.
[Collection(WpfCollection.Name)]
public class CaptureShieldHookTests
{
    [Fact]
    public void Hook_IsReinstalledFromANewThread()
    {
        // 1.ª thread: instala e termina sem desfazer (o Windows desfaz o gancho dela).
        StaRunner.Run(() => CaptureShield.HookNewWindows(), nameof(Hook_IsReinstalledFromANewThread) + " (1.ª thread)");
        var (window, hidden, log) = StaRunner.Run(steps =>
        {
            try
            {
                CaptureShield.WantedOverride = () => CaptureShield.WdaExcludeFromCapture;
                steps.Mark("gancho");
                CaptureShield.HookNewWindows();
                return OpenPopup(steps, TimeSpan.FromSeconds(15), stopWhenHidden: true);
            }
            finally
            {
                CaptureShield.UnhookForTests();
                CaptureShield.WantedOverride = null;
            }
        }, nameof(Hook_IsReinstalledFromANewThread) + " (2.ª thread)", onTimeout: () => CaptureShield.WantedOverride = null);
        Assert.True(window, log);
        Assert.True(hidden, log);
    }

    [Fact]
    public void Unhook_StopsApplying()
    {
        var (window, hidden, log) = StaRunner.Run(steps =>
        {
            try
            {
                CaptureShield.WantedOverride = () => CaptureShield.WdaExcludeFromCapture;
                steps.Mark("gancho");
                CaptureShield.HookNewWindows();
                steps.Mark("desfazer");
                CaptureShield.UnhookForTests();
                // Sem gancho, nada esconde o Popup: espera-se o tempo todo (1 s) a bombear mensagens.
                return OpenPopup(steps, TimeSpan.FromSeconds(1), stopWhenHidden: false);
            }
            finally
            {
                CaptureShield.UnhookForTests();
                CaptureShield.WantedOverride = null;
            }
        }, nameof(Unhook_StopsApplying), onTimeout: () => CaptureShield.WantedOverride = null);
        Assert.True(window, log);
        Assert.False(hidden, log);
    }

    // O mecanismo de ponta a ponta sem o WPF: uma janela Win32 crua (classe STATIC, WS_POPUP) e um ciclo de mensagens
    // próprio. Não passa pelo motor de composição nem pelo lock global dele, por isso a instabilidade do WPF não lhe chega.
    [Fact]
    public void RawWin32Window_IsHiddenFromCapture()
    {
        var (created, hidden, log) = StaRunner.Run(steps =>
        {
            nint hwnd = 0;
            try
            {
                CaptureShield.WantedOverride = () => CaptureShield.WdaExcludeFromCapture;
                steps.Mark("gancho");
                CaptureShield.HookNewWindows();
                steps.Mark("janela.criar");
                hwnd = CreateWindowEx(WsExToolWindow | WsExNoActivate, "STATIC", "teste do CaptureShield", WsPopup,
                    -4000, -4000, 120, 40, 0, 0, 0, 0);
                if (hwnd == 0) return (false, false, steps.Log);
                steps.Mark("janela.mostrar");
                ShowWindow(hwnd, SwShowNa);
                steps.Mark("espera");
                var deadline = steps.Elapsed + TimeSpan.FromSeconds(15);
                var hidden = false;
                while (!hidden && steps.Elapsed < deadline)
                {
                    // Os eventos fora de contexto chegam quando esta thread vai buscar mensagens.
                    while (PeekMessage(out var msg, 0, 0, 0, PmRemove))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                    hidden = CaptureShield.IsExcluded(hwnd);
                    if (!hidden) Thread.Sleep(20);
                }
                steps.Mark(hidden ? "janela.destruir" : "janela.destruir (sem afinidade)");
                return (true, hidden, steps.Log);
            }
            finally
            {
                if (hwnd != 0) DestroyWindow(hwnd);
                CaptureShield.UnhookForTests();
                CaptureShield.WantedOverride = null;
            }
        }, nameof(RawWin32Window_IsHiddenFromCapture), onTimeout: () => CaptureShield.WantedOverride = null);
        Assert.True(created, log);
        Assert.True(hidden, log);
    }

    // Abre um Popup do WPF nesta thread e bombeia mensagens até ficar escondido das capturas (ou até ao fim da espera).
    private static (bool Window, bool Hidden, string Log) OpenPopup(StaSteps steps, TimeSpan wait, bool stopWhenHidden)
    {
        var window = false;
        var hidden = false;
        steps.Mark("popup.criar");
        var popup = new Popup
        {
            Child = new TextBlock { Text = "sugestões do histórico", Width = 120, Height = 40 },
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = 20,
            VerticalOffset = 20,
        };
        steps.Mark("popup.abrir");
        popup.IsOpen = true;
        steps.Mark("espera");
        var frame = new DispatcherFrame();
        var deadline = steps.Elapsed + wait;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) =>
        {
            if (PresentationSource.FromVisual(popup.Child) is HwndSource source)
            {
                window = true;
                hidden = CaptureShield.IsExcluded(source.Handle);
            }
            if ((stopWhenHidden && hidden) || steps.Elapsed > deadline) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        steps.Mark("popup.fechar");
        popup.IsOpen = false;
        return (window, hidden, steps.Log);
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwShowNa = 8;
    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Msg msg);
}
