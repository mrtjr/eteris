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
}
