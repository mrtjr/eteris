using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Tests;

// Sites fixados na barra lateral: lista (ordem, limite, duplicados), separador e ficheiro.
public sealed class WebPanelsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-paineis-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Add_AcceptsWebAddressesOnceAndKeepsTheOrder()
    {
        var list = WebPanels.Add([], " https://web.whatsapp.com/ ", "WhatsApp  Web")!;
        Assert.Equal([new WebPanel("https://web.whatsapp.com/", "WhatsApp Web")], list);
        list = WebPanels.Add(list, "https://pt.wikipedia.org/", "")!;
        Assert.Equal(["https://web.whatsapp.com/", "https://pt.wikipedia.org/"], list.Select(p => p.Url));
        // Repetido, inválido e interno: nada muda.
        Assert.Null(WebPanels.Add(list, "https://web.whatsapp.com/", "outra vez"));
        Assert.Null(WebPanels.Add(list, "eteris://settings/", "Definições"));
        Assert.Null(WebPanels.Add(list, "javascript:alert(1)", "x"));
        Assert.Null(WebPanels.Add(list, null, "x"));
        Assert.True(WebPanels.Contains(list, "https://pt.wikipedia.org/"));
        Assert.False(WebPanels.Contains(list, "https://pt.wikipedia.org/wiki/Fado"));
    }

    [Fact]
    public void Add_StopsAtTheLimit()
    {
        IReadOnlyList<WebPanel> list = [];
        for (var i = 0; i < WebPanels.Max; i++) list = WebPanels.Add(list, $"https://site{i}.pt/", $"Site {i}")!;
        Assert.Equal(WebPanels.Max, list.Count);
        Assert.Null(WebPanels.Add(list, "https://mais-um.pt/", "Mais um"));
    }

    [Fact]
    public void RemoveAndMove_ReorderWithoutLosingAnything()
    {
        IReadOnlyList<WebPanel> list = [new("https://a.pt/", "A"), new("https://b.pt/", "B"), new("https://c.pt/", "C")];
        Assert.Equal(["https://b.pt/", "https://a.pt/", "https://c.pt/"], WebPanels.Move(list, "https://b.pt/", -1).Select(p => p.Url));
        Assert.Equal(["https://b.pt/", "https://c.pt/", "https://a.pt/"], WebPanels.Move(list, "https://a.pt/", 2).Select(p => p.Url));
        // Nas pontas, ou um site que não existe: a lista fica igual.
        Assert.Same(list, WebPanels.Move(list, "https://a.pt/", -1));
        Assert.Same(list, WebPanels.Move(list, "https://c.pt/", 1));
        Assert.Same(list, WebPanels.Move(list, "https://z.pt/", 1));
        Assert.Equal(["https://a.pt/", "https://c.pt/"], WebPanels.Remove(list, "https://b.pt/").Select(p => p.Url));
        Assert.Equal(1, WebPanels.IndexOf(list, "https://b.pt/"));
        Assert.Equal(-1, WebPanels.IndexOf(list, "https://z.pt/"));
    }

    [Fact]
    public void ToPanel_UsesTheWebKeyAndTheHostWhenThereIsNoTitle()
    {
        var panel = WebPanels.ToPanel(new WebPanel("https://www.dicionario.pt/x", ""));
        Assert.Equal("web:https://www.dicionario.pt/x", panel.Key);
        Assert.Equal("dicionario.pt", panel.Title);
        Assert.True(panel.IsWeb);
        Assert.Equal("https://www.dicionario.pt/x", panel.Url);
        Assert.True(SidebarPanels.IsKnownKey(panel.Key));
        Assert.Equal("Chat", WebPanels.ToPanel(new WebPanel("https://a.pt/", "Chat")).Title);
    }

    [Fact]
    public void CleanTitle_CollapsesSpacesAndTrimsLongTitles()
    {
        Assert.Equal("WhatsApp Web", WebPanels.CleanTitle("  WhatsApp \n Web "));
        Assert.Equal(string.Empty, WebPanels.CleanTitle(null));
        var longTitle = WebPanels.CleanTitle(new string('a', 80));
        Assert.Equal(61, longTitle.Length);
        Assert.EndsWith("…", longTitle);
    }

    [Fact]
    public void Serialize_RoundTripsAndParseIsTolerant()
    {
        IReadOnlyList<WebPanel> list = [new("https://a.pt/", "A"), new("https://b.pt/", "")];
        var json = WebPanels.Serialize(list);
        Assert.Contains("\"url\": \"https://a.pt/\"", json);
        Assert.Equal(list, WebPanels.Parse(json));

        Assert.Empty(WebPanels.Parse(null));
        Assert.Empty(WebPanels.Parse("{ not json"));
        Assert.Empty(WebPanels.Parse("[{\"url\":\"eteris://x\",\"title\":\"x\"}]"));
        // Entradas repetidas ou inválidas caem; as boas ficam, na ordem.
        var parsed = WebPanels.Parse("[{\"url\":\"https://a.pt/\",\"title\":\"A\"},{\"url\":\"https://a.pt/\"},{\"url\":\"ftp://b\"},{\"url\":\"https://c.pt/\",\"title\":null}]");
        Assert.Equal(["https://a.pt/", "https://c.pt/"], parsed.Select(p => p.Url));
        Assert.Equal(string.Empty, parsed[1].Title);
    }

    [Fact]
    public void Store_SavesAtomicallyAndLoadsBack()
    {
        var store = new WebPanelStore(_path);
        Assert.Empty(store.Load());
        store.Save([new WebPanel("https://a.pt/", "Á"), new WebPanel("https://b.pt/", "B")]);
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal(["Á", "B"], new WebPanelStore(_path).Load().Select(p => p.Title));
    }
}
