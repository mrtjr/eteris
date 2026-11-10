using System.Runtime.InteropServices;

namespace Eteris.Browser.Infrastructure;

// Chamar a atenção para uma janela do Éteris sem lhe mexer: piscar o botão na barra de tarefas e, quando o Windows
// deixar, trazê-la para a frente. Nunca a restaura nem maximiza. Usado quando uma transferência acaba fora de vista e
// quando a sessão Google fica iniciada no navegador do sistema.
internal static class WindowAttention
{
    // Política pura: uma janela que já está ativa não precisa de chamar a atenção.
    internal static bool ShouldAsk(bool windowActive) => !windowActive;

    // Pisca 3 vezes e fica acesa até a janela voltar ao primeiro plano.
    internal static void Flash(nint hwnd)
    {
        if (hwnd == 0) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG: pisca até a janela voltar ao primeiro plano
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }

    // O Windows costuma recusar o primeiro plano a quem não o tem: então devolve false e fica só o piscar.
    internal static bool TryForeground(nint hwnd) => hwnd != 0 && SetForegroundWindow(hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
