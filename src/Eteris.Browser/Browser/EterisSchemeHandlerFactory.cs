using System.Net;
using System.Text;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Browser;

public sealed class EterisSchemeHandlerFactory : ISchemeHandlerFactory
{
    public const string SchemeName = "eteris";

    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host == "newtab" && uri.AbsolutePath == "/")
        {
            var html = NewTabPage.Render(AppData.TryRead(h => h.TopSites(8)), SearchEngine.Default, DateTime.Now,
                Shields.ShieldStats.SessionBlocked, AppData.Bookmarks(b => b.All(10)), NewTabPage.IsPrivate(request.Url));
            return ResourceHandler.FromString(html, Encoding.UTF8, false, "text/html");
        }

        if (ErrorPage.IsErrorPage(request.Url))
            return ResourceHandler.FromString(ErrorPage.Render(ErrorPage.Code(request.Url), ErrorPage.FailedUrl(request.Url) ?? string.Empty),
                Encoding.UTF8, false, "text/html");

        return ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }
}
