using CefSharp;
using Eteris.Browser.Shields;
using static Eteris.Browser.Tests.PrivacyRequestHandlerTests;
using PrivacyResourceHandler = Eteris.Browser.Shields.PrivacyRequestHandler.PrivacyResourceHandler;

namespace Eteris.Browser.Tests;

// Um pedido bloqueado pelos Escudos falha como num bloqueador de extensão (net::ERR_BLOCKED_BY_CLIENT) e não como um
// pedido cancelado (net::ERR_ABORTED): o código de alguns sites (ge.globo.com, g1.globo.com) ficava à espera de um
// pedido de identificação «abortado» e a página saía incompleta (docs/escudos-paginas-incompletas.md). O pedido continua
// a não sair para a rede: responde-lhe um tratador local que falha logo. Os frames e os websockets ficam como estavam.
public class BlockedByClientTests
{
    [Theory]
    [InlineData(ResourceType.Script, "https://t.example/a.js", true)]
    [InlineData(ResourceType.Xhr, "https://horizon-track.globo.com/id", true)]
    [InlineData(ResourceType.Image, "https://t.example/p.gif", true)]
    [InlineData(ResourceType.Ping, "https://t.example/beacon", true)]
    [InlineData(ResourceType.Stylesheet, "https://t.example/a.css", true)]
    [InlineData(ResourceType.FontResource, "https://t.example/f.woff2", true)]
    [InlineData(ResourceType.Media, "https://t.example/v.mp4", true)]
    [InlineData(ResourceType.SubResource, "https://t.example/x", true)]
    [InlineData(ResourceType.Prefetch, "https://t.example/x", true)]
    [InlineData(ResourceType.MainFrame, "https://t.example/", false)]
    [InlineData(ResourceType.SubFrame, "https://t.example/ad.html", false)]
    [InlineData(ResourceType.Xhr, "wss://t.example/s", false)]
    [InlineData(ResourceType.Xhr, "ws://t.example/s", false)]
    public void Subresources_FailAsBlockedByClient_FramesAndWebSocketsStayCancelled(ResourceType type, string url, bool expected)
        => Assert.Equal(expected, PrivacyResourceHandler.FailsAsBlockedByClient(type, url));

    // Bloqueado: o pedido segue para o tratador local (Continue), que responde ERR_BLOCKED_BY_CLIENT uma só vez.
    [Fact]
    public void ABlockedXhr_GoesToTheLocalHandler_ThatFailsWithBlockedByClient_Once()
    {
        var handler = Handler();
        var request = Request(7, "https://horizon-track.globo.com/id", ResourceType.Xhr);
        Assert.Equal(CefReturnValue.Continue, handler.Settle(CefReturnValue.Cancel, request));
        var local = handler.BlockedHandlerFor(request);
        Assert.NotNull(local);
        Assert.Equal(CefErrorCode.BlockedByClient, ErrorOf(local!));
        Assert.Null(handler.BlockedHandlerFor(request));
    }

    // Um frame bloqueado continua cancelado (sem tratador local), e um pedido permitido nunca recebe o tratador.
    [Fact]
    public void AFrameStaysCancelled_AndAnAllowedRequestIsUntouched()
    {
        var handler = Handler();
        var frame = Request(8, "https://t.example/ad.html", ResourceType.SubFrame);
        Assert.Equal(CefReturnValue.Cancel, handler.Settle(CefReturnValue.Cancel, frame));
        Assert.Null(handler.BlockedHandlerFor(frame));

        var allowed = Request(9, "https://cdn.example/a.js", ResourceType.Script);
        Assert.Equal(CefReturnValue.Continue, handler.Settle(CefReturnValue.Continue, allowed));
        Assert.Null(handler.BlockedHandlerFor(allowed));
        Assert.Equal(CefReturnValue.ContinueAsync, handler.Settle(CefReturnValue.ContinueAsync, allowed));
        Assert.Null(handler.BlockedHandlerFor(allowed));
    }

    // Os bloqueios decididos mais tarde (CNAME, rede local: IRequestCallback) seguem a mesma regra: o callback deixa o
    // pedido seguir para o tratador local em vez de o cancelar; um frame continua cancelado.
    [Fact]
    public void ALateBlock_LetsTheRequestReachTheLocalHandler()
    {
        var handler = Handler();
        Assert.True(handler.ContinueAfterBlock(11, ResourceType.Xhr, "https://t.example/x"));
        Assert.NotNull(handler.BlockedHandlerFor(Request(11, "https://t.example/x", ResourceType.Xhr)));
        Assert.False(handler.ContinueAfterBlock(12, ResourceType.SubFrame, "https://t.example/ad.html"));
        Assert.Null(handler.BlockedHandlerFor(Request(12, "https://t.example/ad.html", ResourceType.SubFrame)));
    }

    // Um pedido marcado que nunca chega ao tratador (a página desistiu dele) não fica para sempre na memória.
    [Fact]
    public void AMarkThatIsNeverUsed_IsForgottenWhenTheRequestEnds()
    {
        var handler = Handler();
        var request = Request(13, "https://t.example/x", ResourceType.Xhr);
        handler.Settle(CefReturnValue.Cancel, request);
        handler.Forget(request);
        Assert.Null(handler.BlockedHandlerFor(request));
    }

    private static PrivacyResourceHandler Handler()
        => new(() => new NoBlocker(), _ => true, _ => { }, null, isPrivate: true);

    private sealed class NoBlocker : IRequestBlocker
    {
        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType) => false;
    }

    private static IRequest Request(ulong id, string url, ResourceType type)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Identifier"] = _ => id;
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        return request;
    }

    private static CefErrorCode ErrorOf(IResourceHandler handler)
    {
        var (response, fake) = Fake<IResponse>.Create();
        var error = CefErrorCode.None;
        fake.Members["set_ErrorCode"] = args =>
        {
            error = (CefErrorCode)args![0]!;
            return null;
        };
        Assert.True(handler.Open(Fake<IRequest>.Create().Proxy, out var handle, Fake<ICallback>.Create().Proxy));
        Assert.True(handle);
        handler.GetResponseHeaders(response, out var length, out var redirect);
        Assert.Equal(0, length);
        Assert.Null(redirect);
        return error;
    }
}
