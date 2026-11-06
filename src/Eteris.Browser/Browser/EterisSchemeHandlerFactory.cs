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
        if (uri is { Host: "newtab", AbsolutePath: "/set" })
        {
            var ok = InternalPages.IsAuthorized(request.Url) && InternalPages.Query(uri, "k") is { } key && key.StartsWith("nt", StringComparison.Ordinal)
                     && InternalPages.Query(uri, "v") is { } value && Settings.Set(key, value);
            return Text(ok ? "ok" : "recusado");
        }
        if (uri is { Host: "newtab", AbsolutePath: "/" })
        {
            var html = NewTabPage.Render(AppData.TryRead(h => h.TopSites(8)), SearchEngine.Preferred, DateTime.Now,
                Shields.ShieldStats.SessionBlocked, AppData.Bookmarks(b => b.All(10)), NewTabPage.IsPrivate(request.Url),
                NewTabPage.IsPrivate(request.Url) ? null : AppData.ShieldWeek(), Settings.Current);
            // A nova aba privada fica sempre escura.
            return ResourceHandler.FromString(InternalPages.Themed(html, InternalPages.LightTheme && !NewTabPage.IsPrivate(request.Url)),
                Encoding.UTF8, false, "text/html");
        }

        if (uri is { Host: "history" }) return History(uri, request.Url);
        if (uri is { Host: "settings" or "about", AbsolutePath: "/clear" }) return ClearData(uri, request.Url, browser);
        if (uri is { Host: "credits", AbsolutePath: "/" }) return Html(CreditsPage.Render());
        if (uri is { Host: "whatsnew", AbsolutePath: "/" }) return Html(WhatsNewPage.Render(SettingsPage.Version));
        if (uri is { Host: "welcome", AbsolutePath: "/" })
            return Html(WelcomePage.Render(Settings.Current, BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
        if (uri is { Host: "settings" or "about" or "welcome" }) return SettingsRoute(uri, request.Url);
        if (uri is { Host: "bookmarks" }) return BookmarksRoute(uri, request.Url);

        if (ErrorPage.IsErrorPage(request.Url))
            return Html(ErrorPage.Render(ErrorPage.Code(request.Url), ErrorPage.FailedUrl(request.Url) ?? string.Empty));

        return ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    private static IResourceHandler SettingsRoute(Uri uri, string url)
    {
        if (uri.AbsolutePath == "/set")
        {
            var ok = InternalPages.IsAuthorized(url)
                     && InternalPages.Query(uri, "k") is { } key && InternalPages.Query(uri, "v") is { } value
                     && Settings.Set(key, value);
            return ResourceHandler.FromString(ok ? "ok" : "recusado", Encoding.UTF8, false, "text/plain");
        }
        if (uri.AbsolutePath == "/update" && InternalPages.IsAuthorized(url))
        {
            _ = Updates.UpdateService.CheckAndDownloadAsync();
            return Text(Updates.UpdateService.Status);
        }
        if (uri.AbsolutePath == "/status") return Text(Updates.UpdateService.Status);
        if (uri.AbsolutePath == "/site-reset" && InternalPages.IsAuthorized(url) && InternalPages.Query(uri, "site") is { Length: > 0 } site)
        {
            AppData.ResetSite(site);
            return Text("ok");
        }
        if (uri.AbsolutePath == "/import" && InternalPages.IsAuthorized(url))
        {
            var source = BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                .FirstOrDefault(s => s.Id == InternalPages.Query(uri, "from"));
            if (source is null) return Text("Navegador não encontrado");
            var added = AppData.ImportBookmarks(BookmarkImport.Read(source.Path));
            return Text(added == 0 ? $"Nada de novo em {source.Name}" : $"{added} favoritos importados de {source.Name}");
        }
        return uri.AbsolutePath == "/"
            ? Html(SettingsPage.Render(Settings.Current, AboutInfo(), focusAbout: uri.Host == "about",
                BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), AppData.SiteSummaries()))
            : ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    private static SettingsPage.About AboutInfo() => new(
        Cef.ChromiumVersion, Cef.CefSharpVersion, Shields.ShieldEngine.Cosmetic is not null,
        Shields.FilterLists.ForApp().Summary(), Updates.UpdateService.Status,
        Updates.UpdateService.State is not (Updates.UpdateState.Development or Updates.UpdateState.Ready));

    private static IResourceHandler Html(string html)
        => ResourceHandler.FromString(InternalPages.Themed(html, InternalPages.LightTheme), Encoding.UTF8, false, "text/html");

    private static IResourceHandler Text(string text) => ResourceHandler.FromString(text, Encoding.UTF8, false, "text/plain");

    // Apaga histórico (no período escolhido), cookies e cache do perfil desta aba.
    private static IResourceHandler ClearData(Uri uri, string url, IBrowser browser)
    {
        if (!InternalPages.IsAuthorized(url)
            || ClearDataRequest.Parse(InternalPages.Query(uri, "what") ?? "", InternalPages.Query(uri, "range") ?? "", DateTime.Now) is not { } request)
            return Text("recusado");
        if (request.History)
            AppData.TryWrite(h => h.DeleteSince(request.Since == DateTime.MinValue ? request.Since : request.Since.ToUniversalTime()));
        if (request.Cookies || request.Cache)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = browser.GetDevToolsClient();
                    if (request.Cookies) await client.Network.ClearBrowserCookiesAsync();
                    if (request.Cache) await client.Network.ClearBrowserCacheAsync();
                }
                catch (Exception) { }
            });
        }
        return Text(request.Summary);
    }

    private static IResourceHandler BookmarksRoute(Uri uri, string url)
    {
        if (uri.AbsolutePath is "/reading-read" or "/reading-remove")
        {
            if (!InternalPages.IsAuthorized(url) || InternalPages.Query(uri, "u") is not { } item) return Text("recusado");
            if (uri.AbsolutePath == "/reading-remove") AppData.RemoveFromReadingList(item);
            else AppData.MarkRead(item, InternalPages.Query(uri, "v") == "true");
            return Text("ok");
        }
        if (uri.AbsolutePath is "/delete" or "/rename")
        {
            if (!InternalPages.IsAuthorized(url) || InternalPages.Query(uri, "u") is not { } target) return Text("recusado");
            if (uri.AbsolutePath == "/delete") AppData.RemoveBookmark(target);
            else if (InternalPages.Query(uri, "v") is { Length: > 0 and <= 300 } title) AppData.RenameBookmark(target, title);
            return Text("ok");
        }
        return uri.AbsolutePath == "/"
            ? Html(BookmarksPage.Render(AppData.Bookmarks(b => b.All(1000)), AppData.ReadingList()))
            : ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
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
        return Html(HistoryPage.Render(AppData.TryRead(h => h.Visits(HistoryPage.MaxVisits)), DateTime.Now));
    }
}
