using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A nos sites fixados: a vista móvel também para o JavaScript da página.
public class ReviewPanelTests
{
    [Fact]
    public void MobileIdentity_SeenByScripts_MatchesTheAgentSentToTheServer()
    {
        // navigator.userAgentData e navigator.platform (pelo DevTools) dizem o mesmo telemóvel que o cabeçalho.
        Assert.Equal("Android", WebPanels.MobilePlatform);
        Assert.Contains($"Android {WebPanels.MobilePlatformVersion.Split('.')[0]};", WebPanels.MobileUserAgent);
        Assert.Contains(WebPanels.MobileModel, WebPanels.MobileUserAgent);
        Assert.Contains($"Chrome/{WebPanels.MobileMajorVersion}.", WebPanels.MobileUserAgent);
        Assert.StartsWith("Linux", WebPanels.MobileNavigatorPlatform);
        var headers = Shields.PrivacyRequestHandler.MobileHeaders(WebPanels.MobileUserAgent);
        Assert.Contains(headers, h => h.Name == "Sec-CH-UA-Platform" && h.Value == $"\"{WebPanels.MobilePlatform}\"");
    }

    [Fact]
    public void MobileAgent_HasTheEngineGeneration()
    {
        // O agente diz a mesma geração do motor que o Chromium embutido (CefSharp 156).
        var cef = typeof(CefSharp.Cef).Assembly.GetName().Version!;
        Assert.Equal(WebPanels.MobileMajorVersion, cef.Major.ToString());
    }
}
