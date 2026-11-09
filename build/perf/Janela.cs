// Janelas e entrada para as medições de desempenho (build/perf/README.md). Carregado com Add-Type por comum.ps1.
// Só atua em janelas dos processos de teste que o script lhe passa; a entrada (roda, teclas, cliques) só sai com a
// janela de teste em primeiro plano e para logo que outra aplicação passe para a frente.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class PerfJanela
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public struct RECT { public int L, T, R, B; }

    public class W
    {
        public IntPtr H; public uint Pid; public string Title; public string Class; public bool Layered;
        public int X, Y, Wd, Ht;
        public override string ToString() { return string.Format("{0} pid={1} [{2},{3} {4}x{5}] {6} {7}", H, Pid, X, Y, Wd, Ht, Class, Title); }
    }

    // Janelas de topo visíveis (todas as aplicações; o script filtra pelos processos de teste).
    public static List<W> List()
    {
        var l = new List<W>();
        EnumWindows((h, p) =>
        {
            if (!IsWindowVisible(h)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            RECT r; GetWindowRect(h, out r);
            l.Add(new W
            {
                H = h, Pid = pid, Title = t.ToString(), Class = c.ToString(),
                Layered = (GetWindowLong(h, -20) & 0x80000) != 0,
                X = r.L, Y = r.T, Wd = r.R - r.L, Ht = r.B - r.T
            });
            return true;
        }, IntPtr.Zero);
        return l;
    }

    // A maior janela visível (sem WS_EX_LAYERED: a camada do Éteris por cima da página não conta) destes processos.
    public static IntPtr Principal(int[] pids)
    {
        var set = new HashSet<uint>();
        foreach (var p in pids) set.Add((uint)p);
        IntPtr best = IntPtr.Zero; long area = 0;
        foreach (var w in List())
        {
            if (!set.Contains(w.Pid) || w.Layered || w.Wd < 300 || w.Ht < 200) continue;
            long a = (long)w.Wd * w.Ht;
            if (a > area) { area = a; best = w.H; }
        }
        return best;
    }

    public static int PidDe(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return (int)pid; }

    public static int PidDaFrente() { return PidDe(GetForegroundWindow()); }

    // Traz a janela para a frente (a tecla F24 solta antes autoriza o SetForegroundWindow vindo de outro processo).
    public static bool Frente(IntPtr h)
    {
        if (GetForegroundWindow() == h) return true;
        keybd_event(0x87, 0, 0, UIntPtr.Zero); keybd_event(0x87, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(h);
        Thread.Sleep(500);
        return PidDaFrente() == PidDe(h);
    }

    // Roda do rato com o cursor parado no centro da janela durante «ms» (um passo de «passo» unidades a cada «intervalo»
    // ms; muda de sentido a cada «virar» passos). Devolve os passos dados; para (devolve negativo) se a janela deixar de
    // estar à frente.
    public static int Roda(IntPtr h, int ms, int intervalo, int passo, int virar)
    {
        if (!Frente(h)) return -1;
        RECT r; GetWindowRect(h, out r);
        SetCursorPos((r.L + r.R) / 2, (r.T + r.B) / 2);
        int pid = PidDe(h), n = 0, sentido = -1;
        var fim = Environment.TickCount + ms;
        while (Environment.TickCount < fim)
        {
            if (PidDaFrente() != pid) return -n - 1;
            mouse_event(0x0800, 0, 0, sentido * passo, UIntPtr.Zero);
            n++;
            if (virar > 0 && n % virar == 0) sentido = -sentido;
            Thread.Sleep(intervalo);
        }
        return n;
    }

    // Uma tecla (código virtual) com a janela de teste à frente.
    public static bool Tecla(IntPtr h, byte vk)
    {
        if (PidDaFrente() != PidDe(h) && !Frente(h)) return false;
        keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero);
        return true;
    }

    // Clique esquerdo em coordenadas relativas à janela de teste.
    public static bool Clique(IntPtr h, int x, int y)
    {
        if (PidDaFrente() != PidDe(h) && !Frente(h)) return false;
        RECT r; GetWindowRect(h, out r);
        SetCursorPos(r.L + x, r.T + y);
        Thread.Sleep(100);
        mouse_event(0x2, 0, 0, 0, UIntPtr.Zero); mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
        return true;
    }
}
