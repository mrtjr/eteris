namespace Eteris.Browser.Data;

// Quando uma página carregada entra no histórico global. Regra base: o documento principal acabou de carregar com uma
// resposta HTTP de sucesso ou redirecionamento (1–399); páginas de erro (4xx/5xx) e falhas de rede não entram.
//
// Exceção, medida ao vivo: um ficheiro de vídeo ou áudio aberto diretamente (mp4, m3u8, aac…) passa a ser um
// «documento de média» — o Chromium entrega o ficheiro ao <video> criado por ele, que o volta a pedir por partes, e
// CANCELA o pedido do documento. A navegação foi feita (a página está à vista e a tocar), mas o fim do carregamento
// vem como abortado: no motor principal o CEF dá FrameLoadEnd com o código -3 (ERR_ABORTED) no lugar do estado HTTP;
// no motor de vídeo o WebView2 dá NavigationCompleted com IsSuccess = false, o estado 200 e ConnectionAborted. Sem
// esta exceção nenhum ficheiro de média aberto diretamente entrava no histórico. O Chrome grava a visita quando a
// navegação é feita (commit), por isso também um carregamento interrompido depois de a página aparecer conta.
public static class HistoryVisit
{
    // net::ERR_ABORTED: o que o CEF põe no lugar do estado HTTP quando o pedido do documento foi cancelado.
    public const int MainEngineAborted = -3;

    private static bool IsOkStatus(int httpStatusCode) => httpStatusCode is > 0 and < 400;

    // Motor principal (FrameLoadEnd do documento principal). «committed»: este endereço chegou a ser o documento da
    // aba (FrameLoadStart, que o CEF dá depois do commit) — sem ele, um -3 é um pedido cancelado antes de haver página.
    public static bool FromMainEngine(int httpStatusCode, bool committed)
        => IsOkStatus(httpStatusCode) || httpStatusCode == MainEngineAborted && committed;

    // Motor de vídeo (NavigationCompleted). «aborted»: o fim veio como ConnectionAborted ou OperationCanceled;
    // «committed»: houve ContentLoading desta navegação e não era uma página de erro.
    public static bool FromVideoEngine(bool success, int httpStatusCode, bool aborted, bool committed)
        => IsOkStatus(httpStatusCode) && (success || aborted && committed);
}
