using CefSharp;

namespace Eteris.Browser.Browser;

// O CefSharp lança uma exceção ao ler Url de um frame já destacado (um iframe removido enquanto um pedido dele está
// a começar). Nas threads do CEF (IO, UI) essa exceção não é apanhada por ninguém e fecha o Éteris inteiro, com
// todas as abas. Todas as leituras de endereço de frames nos tratadores do CEF passam por aqui.
public static class SafeFrame
{
    public static string? Url(IFrame? frame)
    {
        if (frame is null) return null;
        try { return frame.IsValid ? frame.Url : null; }
        catch (Exception) { return null; }
    }

    // O endereço da página principal do separador (o wrapper do frame é libertado logo a seguir).
    public static string? MainUrl(IBrowser? browser)
    {
        if (browser is null) return null;
        try
        {
            if (browser.IsDisposed) return null;
            using var main = browser.MainFrame;
            return Url(main);
        }
        catch (Exception) { return null; }
    }
}
