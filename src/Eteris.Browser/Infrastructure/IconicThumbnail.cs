using System.Runtime.InteropServices;

namespace Eteris.Browser.Infrastructure;

// A miniatura neutra de uma janela privada trancada (MainWindow.PrivateLock.cs): na barra de tarefas, no Alt+Tab e na vista
// de tarefas, o DWM pede-nos a imagem (representação «icónica») em vez de mostrar a janela, e o «espreitar» fica desligado.
// Com a janela à vista a miniatura ao vivo já só mostraria o cartão; o caso que exige isto é a janela MINIMIZADA quando
// tranca: o DWM guarda o instantâneo de antes de minimizar, e o WPF não volta a desenhar uma janela minimizada.
// A imagem é o fundo da janela privada com um cadeado, desenhada aqui píxel a píxel: as funções só recebem tamanhos, nunca
// nada da janela (título, página, ícone). Nada se grava.
public static unsafe class IconicThumbnail
{
    // um/dwmapi.h (o enum DWMWINDOWATTRIBUTE: DWMWA_NCRENDERING_ENABLED = 1 e os seguintes um por linha).
    public const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    public const int DWMWA_HAS_ICONIC_BITMAP = 10;
    public const int DWMWA_DISALLOW_PEEK = 11;
    // O recurso descrito na especificação (§2.4.6) se um fotograma antigo aparecer ao restaurar; por agora sem uso.
    public const int DWMWA_CLOAK = 13;

    // um/WinUser.h.
    public const int WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    public const int WM_DWMSENDICONICLIVEPREVIEWBITMAP = 0x0326;

    // O fundo da janela privada (MainWindow.Private.cs, ConfigurePrivateWindow), igual nos dois temas, e o cadeado (BGRA
    // opaco, 0xAARRGGBB), com um contraste de cerca de 9:1 (IconicThumbnailTests exige pelo menos 3:1).
    public const uint Background = 0xFF140B24;
    public const uint Lock = 0xFFB9A7FF;

    // O lParam de WM_DWMSENDICONICTHUMBNAIL: a largura máxima na palavra alta, a altura máxima na baixa.
    public static (int Width, int Height) MaxSize(nint lParam)
        => ((int)(((long)lParam >> 16) & 0xFFFF), (int)((long)lParam & 0xFFFF));

    // A proporção da janela dentro do máximo, nunca menos de 1×1. Sem tamanho da janela (minimizada), ocupa o máximo.
    public static (int Width, int Height) Fit(int maxWidth, int maxHeight, int windowWidth, int windowHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        if (windowWidth <= 0 || windowHeight <= 0) return (maxWidth, maxHeight);
        var scale = Math.Min((double)maxWidth / windowWidth, (double)maxHeight / windowHeight);
        return (Math.Clamp((int)Math.Round(windowWidth * scale), 1, maxWidth), Math.Clamp((int)Math.Round(windowHeight * scale), 1, maxHeight));
    }

    // A imagem neutra, linha a linha de cima para baixo: o fundo e, no centro, um cadeado feito de retângulos (o corpo e o
    // arco). Sem texto.
    public static uint[] Pixels(int width, int height)
    {
        if (width <= 0 || height <= 0) return [];
        var pixels = new uint[width * height];
        Draw(pixels, width, height);
        return pixels;
    }

    private static void Draw(Span<uint> pixels, int width, int height)
    {
        pixels.Fill(Background);
        var size = Math.Min(width, height);
        var (cx, cy) = (width / 2, height / 2);
        var bodyWidth = Math.Max(1, (int)Math.Round(size * 0.44));
        var bodyHeight = Math.Max(1, (int)Math.Round(size * 0.34));
        var bodyLeft = cx - bodyWidth / 2;
        var bodyTop = cy - bodyHeight / 3;
        Fill(pixels, width, height, bodyLeft, bodyTop, bodyWidth, bodyHeight);
        var arcWidth = Math.Max(1, (int)Math.Round(size * 0.30));
        var arcHeight = Math.Max(1, (int)Math.Round(size * 0.22));
        var stroke = Math.Max(1, (int)Math.Round(size * 0.07));
        var arcLeft = cx - arcWidth / 2;
        var arcTop = bodyTop - arcHeight;
        Fill(pixels, width, height, arcLeft, arcTop, stroke, arcHeight);
        Fill(pixels, width, height, arcLeft + arcWidth - stroke, arcTop, stroke, arcHeight);
        Fill(pixels, width, height, arcLeft, arcTop, arcWidth, stroke);
    }

    private static void Fill(Span<uint> pixels, int width, int height, int left, int top, int w, int h)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + h); y++)
            for (var x = Math.Max(0, left); x < Math.Min(width, left + w); x++)
                pixels[y * width + x] = Lock;
    }

    // Trancar (on) ou destrancar: o DWM passa a pedir a miniatura e a pré-visualização a nós, sem «espreitar», e esquece as
    // que tinha guardadas.
    public static void Neutralize(nint hwnd, bool on)
    {
        if (hwnd == 0) return;
        var value = on ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, &value, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, &value, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_DISALLOW_PEEK, &value, sizeof(int));
            DwmInvalidateIconicBitmaps(hwnd);
        }
        catch (Exception) { }
    }

    // O gancho da janela (HwndSource.AddHook), só com a janela trancada: responde aos dois pedidos do DWM com a imagem neutra.
    public static bool Answer(nint hwnd, int message, nint lParam)
    {
        if (message == WM_DWMSENDICONICTHUMBNAIL)
        {
            var (maxWidth, maxHeight) = MaxSize(lParam);
            var (windowWidth, windowHeight) = ClientSize(hwnd);
            var (width, height) = Fit(maxWidth, maxHeight, windowWidth, windowHeight);
            WithBitmap(width, height, bitmap => DwmSetIconicThumbnail(hwnd, bitmap, 0));
            return true;
        }
        if (message == WM_DWMSENDICONICLIVEPREVIEWBITMAP)
        {
            var (width, height) = ClientSize(hwnd);
            WithBitmap(Math.Max(1, width), Math.Max(1, height), bitmap => DwmSetIconicLivePreviewBitmap(hwnd, bitmap, 0, 0));
            return true;
        }
        return false;
    }

    private static (int Width, int Height) ClientSize(nint hwnd)
        => GetClientRect(hwnd, out var rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : (0, 0);

    // Um DIB de 32 bits de cima para baixo, desenhado diretamente na memória dele, entregue ao DWM e apagado.
    private static void WithBitmap(int width, int height, Func<nint, int> give)
    {
        var info = new BitmapInfo
        {
            Size = (uint)sizeof(BitmapInfo) - sizeof(uint),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(0, &info, 0, out var bits, 0, 0);
        if (bitmap == 0) return;
        try
        {
            if (bits == 0) return;
            Draw(new Span<uint>((void*)bits, width * height), width, height);
            give(bitmap);
        }
        catch (Exception) { }
        finally { DeleteObject(bitmap); }
    }

    // BITMAPINFO: o BITMAPINFOHEADER (40 bytes) e uma entrada da tabela de cores (sem uso com BI_RGB a 32 bits).
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmInvalidateIconicBitmaps(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetIconicThumbnail(nint hwnd, nint bitmap, int flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetIconicLivePreviewBitmap(nint hwnd, nint bitmap, nint clientOrigin, int flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, BitmapInfo* info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
}
