using CefSharp;

namespace Eteris.Browser.Shields;

// Modo rigoroso: além das listas, nenhum script nem frame de outro domínio corre na página.
public static class StrictPolicy
{
    public static bool ShouldBlock(string pageUrl, string requestUrl, ResourceType type)
    {
        if (type is not (ResourceType.Script or ResourceType.SubFrame or ResourceType.Object or ResourceType.Worker or ResourceType.SharedWorker))
            return false;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request))
            return false;
        return !string.Equals(TrackerBlocker.RegistrableDomain(page.Host), TrackerBlocker.RegistrableDomain(request.Host),
            StringComparison.OrdinalIgnoreCase);
    }
}
