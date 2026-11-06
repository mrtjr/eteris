using CefSharp;

namespace Eteris.Browser.Shields;

public static class ResourceTypes
{
    // Nomes dos tipos de pedido usados pelas listas de filtros ($script, $image, $xmlhttprequest…).
    public static string ToAdblock(ResourceType type) => type switch
    {
        ResourceType.MainFrame => "document",
        ResourceType.SubFrame => "sub_frame",
        ResourceType.Script or ResourceType.Worker or ResourceType.SharedWorker or ResourceType.ServiceWorker => "script",
        ResourceType.Stylesheet => "stylesheet",
        ResourceType.Image or ResourceType.Favicon => "image",
        ResourceType.FontResource => "font",
        ResourceType.Media => "media",
        ResourceType.Xhr => "xmlhttprequest",
        ResourceType.Ping => "ping",
        ResourceType.Object or ResourceType.PluginResource => "object",
        _ => "other",
    };
}
