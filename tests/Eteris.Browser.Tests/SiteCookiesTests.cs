using CefSharp;

namespace Eteris.Browser.Tests;

// Revisão final do plano 6: o CefSharp devolve -1 quando não chega aos cookies (MainWindow.ClearData.cs). No Triturar,
// no Esquecer ao fechar e no cadeado, um -1 conta como falha (o aviso diz que ficaram cookies), e não estreita o âmbito:
// os outros cookies e os anfitriões deles seguem.
public class SiteCookiesTests
{
    [Fact]
    public async Task DeleteSiteCookies_ANegativeAnswerIsAFailure_AndTheRestGoes()
    {
        var manager = new FakeCookies(
            [("exemplo.pt", "a"), (".exemplo.pt", "b"), ("contas.exemplo.pt", "c"), ("outro.pt", "d")],
            refuse: "b");
        var (deleted, hosts, failed) = await MainWindow.DeleteSiteCookiesAsync(manager, "exemplo.pt");
        Assert.True(failed);
        Assert.Equal(2, deleted);
        Assert.Equal(["a", "b", "c"], manager.Asked.Order(StringComparer.Ordinal));
        Assert.Contains("contas.exemplo.pt", hosts);
    }

    [Fact]
    public async Task DeleteSiteCookies_AllDeleted_IsNoFailure()
    {
        var manager = new FakeCookies([("exemplo.pt", "a")], refuse: null);
        var (deleted, _, failed) = await MainWindow.DeleteSiteCookiesAsync(manager, "exemplo.pt");
        Assert.False(failed);
        Assert.Equal(1, deleted);
    }

    private sealed class FakeCookies(IReadOnlyList<(string Domain, string Name)> cookies, string? refuse) : ICookieManager
    {
        public List<string> Asked { get; } = [];
        public bool IsDisposed => false;

        public bool DeleteCookies(string url = null!, string name = null!, IDeleteCookiesCallback callback = null!)
        {
            Asked.Add(name);
            callback.OnComplete(name == refuse ? -1 : 1);
            return true;
        }

        public bool VisitAllCookies(ICookieVisitor visitor)
        {
            for (var i = 0; i < cookies.Count; i++)
            {
                var delete = false;
                visitor.Visit(new Cookie { Domain = cookies[i].Domain, Name = cookies[i].Name, Path = "/" }, i, cookies.Count, ref delete);
            }
            visitor.Dispose();
            return true;
        }

        public bool SetCookie(string url, Cookie cookie, ISetCookieCallback callback = null!) => false;
        public bool VisitUrlCookies(string url, bool includeHttpOnly, ICookieVisitor visitor) => false;
        public bool FlushStore(ICompletionCallback callback) => false;
        public void Dispose() { }
    }
}
