using CefSharp;

namespace Eteris.Browser.Shields;

// A resposta local a um pedido que os Escudos bloquearam: falha logo com net::ERR_BLOCKED_BY_CLIENT, o erro de um
// bloqueador de extensão (uBlock Origin, os Shields do Brave), sem nada sair para a rede. Um pedido só cancelado
// (net::ERR_ABORTED) deixava o código de alguns sites à espera e a página incompleta
// (docs/escudos-paginas-incompletas.md). Como o NameNotResolvedHandler da página de auto-teste: o ErrorCode do
// ResourceHandler do CefSharp não chega à resposta, por isso o erro vai na própria resposta (IResponse.ErrorCode).
internal sealed class BlockedByClientHandler : IResourceHandler
{
    public bool Open(IRequest request, out bool handleRequest, ICallback callback)
    {
        callback.Dispose();
        handleRequest = true;
        return true;
    }

    public void GetResponseHeaders(IResponse response, out long responseLength, out string? redirectUrl)
    {
        response.ErrorCode = CefErrorCode.BlockedByClient;
        responseLength = 0;
        redirectUrl = null;
    }

    public bool Skip(long bytesToSkip, out long bytesSkipped, CefSharp.Callback.IResourceSkipCallback callback)
    {
        callback.Dispose();
        bytesSkipped = -2;
        return false;
    }

    public bool Read(System.IO.Stream dataOut, out int bytesRead, CefSharp.Callback.IResourceReadCallback callback)
    {
        callback.Dispose();
        bytesRead = 0;
        return false;
    }

    public bool ProcessRequest(IRequest request, ICallback callback) => false;

    public bool ReadResponse(System.IO.Stream dataOut, out int bytesRead, ICallback callback)
    {
        bytesRead = 0;
        return false;
    }

    public void Cancel() { }

    public void Dispose() { }
}
