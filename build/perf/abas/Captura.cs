// Captura de alta frequência (60 fps, reduzida por «div») da área do ecrã de uma janela, para contar os fotogramas
// errados ao trocar de abas (B-ABAS). Regista também a janela em primeiro plano em cada fotograma (.fg): o analisar.py
// descarta as capturas em que a janela medida não esteve sempre à frente. Carregado por abas\sessao.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class PerfCaptura
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    static Thread _t; static volatile bool _stop;
    static List<byte[]> _frames; static List<long> _ticks; static List<long> _fg; static int _w, _h;

    // Captura a área do ecrã do retângulo dado (pixels físicos), reduzida por 'div', ao ritmo 'fps', até Stop().
    public static void Start(int left, int top, int width, int height, int div, int fps, int maxFrames)
    {
        _stop = false; _frames = new List<byte[]>(); _ticks = new List<long>(); _fg = new List<long>();
        _w = width / div; _h = height / div;
        timeBeginPeriod(1);
        _t = new Thread(() =>
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32 };
            IntPtr bits; IntPtr dib = CreateDIBSection(mem, ref bi, 0, out bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mem, dib);
            SetStretchBltMode(mem, 3); // COLORONCOLOR
            var sw = Stopwatch.StartNew(); long period = Stopwatch.Frequency / fps; long next = 0;
            while (!_stop && _frames.Count < maxFrames)
            {
                StretchBlt(mem, 0, 0, _w, _h, screen, left, top, width, height, 0x00CC0020);
                var buf = new byte[_w * _h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                _frames.Add(buf); _ticks.Add(DateTime.UtcNow.Ticks); _fg.Add(GetForegroundWindow().ToInt64());
                next += period;
                while (sw.ElapsedTicks < next) { if (next - sw.ElapsedTicks > Stopwatch.Frequency / 500) Thread.Sleep(1); }
            }
            SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }) { IsBackground = true, Priority = ThreadPriority.Highest };
        _t.Start();
    }

    public static int Stop(string outPrefix)
    {
        _stop = true; _t.Join();
        using (var f = File.Create(outPrefix + ".raw")) foreach (var b in _frames) f.Write(b, 0, b.Length);
        using (var w = new StreamWriter(outPrefix + ".txt")) { w.WriteLine(_w + " " + _h + " " + _frames.Count); foreach (var t in _ticks) w.WriteLine(t); }
        File.WriteAllLines(outPrefix + ".fg", _fg.ConvertAll(x => x.ToString()));
        int n = _frames.Count; _frames = null; GC.Collect();
        return n;
    }
}
