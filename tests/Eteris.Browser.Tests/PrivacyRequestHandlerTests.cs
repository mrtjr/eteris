using System.Reflection;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;
using PrivacyResourceHandler = Eteris.Browser.Shields.PrivacyRequestHandler.PrivacyResourceHandler;

namespace Eteris.Browser.Tests;

// O tratador de pedidos corre na thread IO do CEF: uma exceção (frame destacado) fechava o Éteris inteiro.
public class PrivacyRequestHandlerTests
{
    // Objeto falso para interfaces do CefSharp: cada membro lido devolve o que o teste definir (ou o valor por omissão).
    public class Fake<T> : DispatchProxy where T : class
    {
        public Dictionary<string, Func<object?[]?, object?>> Members { get; } = new();

        public static (T Proxy, Fake<T> Fake) Create()
        {
            var proxy = DispatchProxy.Create<T, Fake<T>>();
            return (proxy, (Fake<T>)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is not null && Members.TryGetValue(method.Name, out var member)) return member(args);
            return method?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class Blocker(bool block) : IRequestBlocker
    {
        public string? LastDocument;
        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType)
        {
            LastDocument = pageUrl;
            return block;
        }
    }

    private static IFrame DetachedFrame(bool validButThrows = false)
    {
        var (frame, fake) = Fake<IFrame>.Create();
        fake.Members["get_IsValid"] = _ => validButThrows;
        fake.Members["get_Url"] = _ => throw new Exception("The underlying frame is no longer valid - please check the IsValid property before calling!");
        return frame;
    }

    private static IBrowser BrowserWithMain(IFrame main)
    {
        var (browser, fake) = Fake<IBrowser>.Create();
        fake.Members["get_MainFrame"] = _ => main;
        return browser;
    }

    private static IRequest Request(string url, ResourceType type)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        return request;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedFrame_ContinuesInsteadOfThrowing(bool validButThrows)
    {
        var blocker = new Blocker(block: false);
        var handler = new PrivacyResourceHandler(() => blocker, _ => true, _ => { }, null);
        var frame = DetachedFrame(validButThrows);
        var result = handler.Decide(BrowserWithMain(frame), frame, Request("https://ads.example/a.js", ResourceType.Script));
        Assert.Equal(CefReturnValue.Continue, result);
        Assert.Null(blocker.LastDocument);
    }

    [Fact]
    public void DetachedFrame_StillBlocksWhatTheListsBlock()
    {
        var blocked = new List<string>();
        var handler = new PrivacyResourceHandler(() => new Blocker(block: true), _ => true, blocked.Add, null);
        var frame = DetachedFrame();
        var result = handler.Decide(BrowserWithMain(frame), frame, Request("https://ads.example/a.js", ResourceType.Script));
        Assert.Equal(CefReturnValue.Cancel, result);
        Assert.Equal(["ads.example"], blocked);
    }

    [Fact]
    public void ExceptionAfterDecidingToBlock_StillCancels()
    {
        var handler = new PrivacyResourceHandler(() => new Blocker(block: true), _ => true,
            _ => throw new InvalidOperationException("janela a fechar"), null);
        var frame = DetachedFrame();
        Assert.Equal(CefReturnValue.Cancel,
            handler.Decide(BrowserWithMain(frame), frame, Request("https://ads.example/a.js", ResourceType.Script)));
    }

    [Theory]
    [InlineData("eteris://newtab/")]
    [InlineData("devtools://devtools/inspector.html")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("blob:https://site.pt/123")]
    public void NonNetworkRequests_SkipTheLists(string url)
    {
        var blocker = new Blocker(block: true);
        var handler = new PrivacyResourceHandler(() => blocker, _ => true, _ => { }, null);
        var frame = DetachedFrame();
        Assert.Equal(CefReturnValue.Continue, handler.Decide(BrowserWithMain(frame), frame, Request(url, ResourceType.Image)));
    }

    [Fact]
    public void SafeFrame_ReadsValidFrames_AndNeverThrows()
    {
        var (frame, fake) = Fake<IFrame>.Create();
        fake.Members["get_IsValid"] = _ => true;
        fake.Members["get_Url"] = _ => "https://site.pt/";
        Assert.Equal("https://site.pt/", SafeFrame.Url(frame));
        Assert.Equal("https://site.pt/", SafeFrame.MainUrl(BrowserWithMain(frame)));
        Assert.Null(SafeFrame.Url(DetachedFrame()));
        Assert.Null(SafeFrame.Url(null));
        var (broken, brokenFake) = Fake<IBrowser>.Create();
        brokenFake.Members["get_MainFrame"] = _ => throw new ObjectDisposedException("browser");
        Assert.Null(SafeFrame.MainUrl(broken));
    }
}
