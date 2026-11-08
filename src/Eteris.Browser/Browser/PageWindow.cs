namespace Eteris.Browser.Browser;

// Regras puras das janelas de uma página: a «static» que o CefSharp cria para alojar a do CEF (CefBrowserWindow), que
// por sua vez aloja a do Chromium (Chrome_WidgetWin). Usadas por Browser/PageBrowser.cs e MainWindow.PageVisibility.cs.
public static class PageWindow
{
    public const int WmPaint = 0x000F;
    public const int WmEraseBkgnd = 0x0014;

    public enum Paint
    {
        // Mensagem que não é de desenho: segue para o tratamento normal.
        Pass,
        // WM_ERASEBKGND: diz que apagou, sem pintar nada.
        SkipErase,
        // WM_PAINT: dá a área por desenhada, sem pintar nada.
        Validate,
    }

    // A static só se vê quando a janela do CEF ainda não a cobre (aba nova antes de o CEF criar a sua janela, aba
    // escondida encolhida a 0×0 a voltar). Se pinta, pinta por GDI com o pincel de controlo do sistema (cinzento) e,
    // com o Mica, por cima da área de vidro: o GDI escreve alfa 0 e o DWM mostra o Mica no lugar da página. Sem pintar,
    // fica o que o WPF lá pôs — a cor de fundo da página (Theme/PageBackdrop.cs) —, com ou sem Mica.
    public static Paint PaintStep(int message) => message switch
    {
        WmEraseBkgnd => Paint.SkipErase,
        WmPaint => Paint.Validate,
        _ => Paint.Pass,
    };

    // Tamanho em píxeis que o CefSharp dá à janela do CEF para um tamanho WPF (ChromiumWebBrowser.ResizeBrowser:
    // a parte inteira de ActualWidth/ActualHeight e, acima de 100 %, multiplicada pela escala e arredondada). Quem
    // põe a janela da página no tamanho final antes do CefSharp tem de chegar exatamente ao mesmo número, senão o
    // CefSharp volta a redimensioná-la (mais uma mudança de tamanho para o Chromium).
    public static (int Width, int Height) PixelSize(double width, double height, double dpiScale)
    {
        var w = Whole(width);
        var h = Whole(height);
        if (dpiScale > 1.0)
        {
            w = (int)Math.Round(w * dpiScale);
            h = (int)Math.Round(h * dpiScale);
        }
        return (w, h);
    }

    // Tamanho com que a janela do CEF nasce: o da área das páginas, quando já se sabe; senão o que o CefSharp leu da
    // static (0×0 numa aba acabada de criar, porque o WPF ainda não a dispôs).
    public static (int Width, int Height) BirthSize((int Width, int Height) fromStatic, (int Width, int Height)? pageArea)
        => pageArea is { Width: > 0, Height: > 0 } area ? area : fromStatic;

    public enum Restore
    {
        // Já à vista (ou ainda sem janela): nada a fazer.
        None,
        // Num só passo e já no tamanho final: SetWindowPlacement com rcNormalPosition = área da página e
        // SW_SHOWNOACTIVATE. O Chromium vê uma só mudança de tamanho (de minimizada para o tamanho final).
        Placement,
        // Tamanho por saber: restaurar e deixar o CefSharp acertar o tamanho (duas mudanças, como antes).
        ShowOnly,
    }

    // Como voltar a mostrar a janela do Chromium de uma aba escondida (minimizada). Restaurar com ShowWindow repõe o
    // último tamanho «normal», que pode ser o 0×0 de quando o CefSharp a encolheu; corrigir a seguir com SetWindowPos é
    // uma 2.ª mudança de tamanho com a página ainda «hidden» — o Chromium avança a superfície e mostra a página em
    // branco (DelegatedFrameHost::EmbedSurface; estudo do Chromium, §3.1).
    public static Restore RestoreStep(bool minimized, (int Width, int Height) size)
        => !minimized ? Restore.None : size is { Width: > 0, Height: > 0 } ? Restore.Placement : Restore.ShowOnly;

    private static int Whole(double value) => double.IsFinite(value) && value > 0 ? (int)Math.Min(value, int.MaxValue) : 0;
}
