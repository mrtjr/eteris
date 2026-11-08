using System.Text;
using CefSharp;

namespace Eteris.Browser.Browser;

// O «servidor» do auto-teste de privacidade: responde a https://autoteste-eteris.invalid/ e a
// https://terceiro-eteris.invalid/ dentro do próprio Éteris (fábrica por nome, registada no contexto), e anota os
// cabeçalhos que recebeu para o veredicto. Nada vai à rede: os nomes .invalid nunca existem na Internet. Só a página de
// teste fala com eles (SelfTest.Serves); para qualquer outra página falham como um nome inexistente.
public sealed class SelfTestSchemeHandlerFactory : ISchemeHandlerFactory
{
    public static void Register(IRequestContext context)
    {
        var factory = new SelfTestSchemeHandlerFactory();
        context.RegisterSchemeHandlerFactory("https", SelfTest.Site, factory);
        context.RegisterSchemeHandlerFactory("https", SelfTest.ThirdParty, factory);
    }

    // Uma exceção num tratador do CEF fecha o Éteris inteiro: aqui nunca passa.
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        try
        {
            var main = SafeFrame.MainUrl(browser);
            var mainFrame = frame is not null && request.ResourceType == ResourceType.MainFrame && IsMain(frame);
            var transition = request.TransitionType;
            var explicitOrHistory = (transition & TransitionType.SourceMask) == TransitionType.Explicit
                                    || (transition & TransitionType.ForwardBack) == TransitionType.ForwardBack;
            if (browser is null || !SelfTest.Serves(main, request.Url, mainFrame, explicitOrHistory)) return NotResolved();
            return Serve(request);
        }
        catch (Exception) { return NotResolved(); }
    }

    private static bool IsMain(IFrame frame)
    {
        try { return frame.IsValid && frame.IsMain; }
        catch (Exception) { return false; }
    }

    // O mesmo erro que o Firefox, o Brave e o Chrome dão para um nome .invalid, sem ir à rede.
    private static IResourceHandler NotResolved() => new NameNotResolvedHandler();

    // O ErrorCode do ResourceHandler do CefSharp não chega à resposta (medido: um fetch() de outra página resolvia, com
    // uma resposta 200 vazia). O erro vai na própria resposta (IResponse.ErrorCode), sem corpo.
    private sealed class NameNotResolvedHandler : IResourceHandler
    {
        public bool Open(IRequest request, out bool handleRequest, ICallback callback)
        {
            callback.Dispose();
            handleRequest = true;
            return true;
        }

        public void GetResponseHeaders(IResponse response, out long responseLength, out string? redirectUrl)
        {
            response.ErrorCode = CefErrorCode.NameNotResolved;
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

    private static IResourceHandler Serve(IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !SelfTest.IsTestHost(uri.Host))
            return ResourceHandler.ForErrorMessage("Não encontrado", System.Net.HttpStatusCode.NotFound);
        var headers = request.Headers.AllKeys.Where(k => k is not null)
            .ToDictionary(k => k!, k => request.Headers[k] ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        // O Referer não vem nos cabeçalhos do pedido no CEF: lê-se do próprio pedido.
        if (!string.IsNullOrEmpty(request.ReferrerUrl)) headers["Referer"] = request.ReferrerUrl;
        var run = InternalPages.Query(uri, "run") ?? string.Empty;
        switch (uri.AbsolutePath)
        {
            case "/" when uri.Host == SelfTest.Site:
                SelfTest.Record("main", headers);
                return Html(SelfTest.PageHtml);
            case "/set":
                var set = Html("ok");
                set.Headers.Add("Set-Cookie", $"{SelfTest.CookieName}={run}; Path=/; Max-Age=120; Secure; SameSite=None");
                return set;
            case "/eco":
                SelfTest.Record(run + "/" + (InternalPages.Query(uri, "k") ?? string.Empty), headers);
                return Html("ok");
            default:
                return ResourceHandler.ForErrorMessage("Não encontrado", System.Net.HttpStatusCode.NotFound);
        }
    }

    private static ResourceHandler Html(string html)
    {
        // Um ResourceHandler próprio (o de FromString não aceita cabeçalhos): é por aqui que vai o Set-Cookie do teste.
        var handler = new ResourceHandler("text/html", new System.IO.MemoryStream(Encoding.UTF8.GetBytes(html)), autoDisposeStream: true, charset: "utf-8");
        // Nada desta página fica na cache do disco.
        handler.Headers.Add("Cache-Control", "no-store");
        return handler;
    }
}
