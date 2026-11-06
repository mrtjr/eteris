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
        Uri.TryCreate(request.Url, UriKind.Absolute, out var uri);
        if (uri is { Host: "newtab", AbsolutePath: "/" })
        {
            var html = NewTabPage.Render(AppData.TryRead(h => h.TopSites(8)), SearchEngine.Default, DateTime.Now,
                Shields.ShieldStats.SessionBlocked, AppData.Bookmarks(b => b.All(10)), NewTabPage.IsPrivate(request.Url));
            return ResourceHandler.FromString(html, Encoding.UTF8, false, "text/html");
        }

        if (uri is { Host: "history" }) return History(uri, request.Url);

        if (ErrorPage.IsErrorPage(request.Url))
            return ResourceHandler.FromString(ErrorPage.Render(ErrorPage.Code(request.Url), ErrorPage.FailedUrl(request.Url) ?? string.Empty),
                Encoding.UTF8, false, "text/html");

        return ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    private static IResourceHandler History(Uri uri, string url)
    {
        switch (uri.AbsolutePath)
        {
            case "/delete" when HistoryPage.IsAuthorized(url) && long.TryParse(HistoryPage.Query(uri, "id"), out var id):
                AppData.TryWrite(h => h.DeleteVisit(id));
                return ResourceHandler.FromString("ok", Encoding.UTF8, false, "text/plain");
            case "/clear" when HistoryPage.IsAuthorized(url)
                               && HistoryPage.RangeStart(HistoryPage.Query(uri, "range") ?? "", DateTime.Now) is { } start:
                AppData.TryWrite(h => h.DeleteSince(start == DateTime.MinValue ? start : start.ToUniversalTime()));
                break;
            case "/" or "/clear":
                break;
            default:
                return ResourceHandler.ForErrorMessage("Pedido recusado", HttpStatusCode.Forbidden);
        }
        var html = HistoryPage.Render(AppData.TryRead(h => h.Visits(HistoryPage.MaxVisits)), DateTime.Now);
        return ResourceHandler.FromString(html, Encoding.UTF8, false, "text/html");
    }
}
