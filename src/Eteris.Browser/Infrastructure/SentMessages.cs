using System.Runtime.InteropServices;

namespace Eteris.Browser.Infrastructure;

// Entrega ao fio atual só as mensagens enviadas por outras threads (SendMessage), sem tirar nada da fila: nenhuma
// mensagem do WPF (entrada, desenho, tarefas do Dispatcher) corre aqui dentro.
public static class SentMessages
{
    // Espera por «done» (até «limit») e, enquanto espera, recebe as mensagens que outras threads enviam a janelas deste
    // fio (as da criação da janela do CEF). Devolve true se «done» chegou.
    public static bool DeliverUntil(WaitHandle done, TimeSpan limit)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var handles = new[] { done.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            var left = limit - clock.Elapsed;
            if (left <= TimeSpan.Zero) return done.WaitOne(0);
            var result = MsgWaitForMultipleObjectsEx(1, handles, (uint)Math.Ceiling(left.TotalMilliseconds), QsSendMessage, 0);
            if (result == WaitObject0) return true;
            if (result == WaitObject0 + 1) PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove | PmQsSendMessage);
            else if (result != WaitTimeout) return done.WaitOne(0);
        }
    }

    private const uint QsSendMessage = 0x0040;
    private const uint PmNoRemove = 0x0000;
    private const uint PmQsSendMessage = QsSendMessage << 16;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);
}
