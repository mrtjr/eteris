using CefSharp;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class ResourceTypesTests
{
    [Theory]
    [InlineData(ResourceType.MainFrame, "document")]
    [InlineData(ResourceType.SubFrame, "sub_frame")]
    [InlineData(ResourceType.Script, "script")]
    [InlineData(ResourceType.Stylesheet, "stylesheet")]
    [InlineData(ResourceType.Image, "image")]
    [InlineData(ResourceType.Favicon, "image")]
    [InlineData(ResourceType.FontResource, "font")]
    [InlineData(ResourceType.Media, "media")]
    [InlineData(ResourceType.Xhr, "xmlhttprequest")]
    [InlineData(ResourceType.Ping, "ping")]
    [InlineData(ResourceType.Object, "object")]
    [InlineData(ResourceType.Worker, "script")]
    [InlineData(ResourceType.ServiceWorker, "script")]
    [InlineData(ResourceType.SubResource, "other")]
    [InlineData(ResourceType.Prefetch, "other")]
    public void ToAdblock_MapsCefTypesToFilterTypes(ResourceType type, string expected)
        => Assert.Equal(expected, ResourceTypes.ToAdblock(type));
}
