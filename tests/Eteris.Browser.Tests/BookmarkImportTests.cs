using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class BookmarkImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-import-" + Guid.NewGuid());

    private const string ChromiumJson = """
        {"roots":{
          "bookmark_bar":{"type":"folder","name":"Barra","children":[
            {"type":"url","name":"Público","url":"https://www.publico.pt/"},
            {"type":"folder","name":"Dev","children":[
              {"type":"url","name":"GitHub","url":"https://github.com/"},
              {"type":"url","name":"Script","url":"javascript:alert(1)"}]}]},
          "other":{"type":"folder","name":"Outros","children":[
            {"type":"url","name":"","url":"https://example.com/"}]},
          "synced":{"type":"folder","name":"Telemóvel","children":[]}}}
        """;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Parse_FlattensFoldersAndSkipsNonWebLinks()
    {
        var marks = BookmarkImport.ParseChromium(ChromiumJson);
        Assert.Equal(["https://www.publico.pt/", "https://github.com/", "https://example.com/"], marks.Select(m => m.Url));
        Assert.Equal("example.com", marks[2].Title);
    }

    [Fact]
    public void Parse_BadFile_GivesNothing()
        => Assert.Empty(BookmarkImport.ParseChromium("{ nope"));

    [Fact]
    public void Detect_FindsInstalledChromiumBrowsers()
    {
        var edge = Path.Combine(_root, "Microsoft", "Edge", "User Data", "Default");
        Directory.CreateDirectory(edge);
        File.WriteAllText(Path.Combine(edge, "Bookmarks"), ChromiumJson);
        var sources = BookmarkImport.Detect(_root);
        var source = Assert.Single(sources);
        Assert.Equal("edge", source.Id);
        Assert.Equal("Microsoft Edge", source.Name);
        Assert.Equal(3, source.Count);
    }

    [Fact]
    public void Import_AddsOnlyMissingBookmarks()
    {
        Directory.CreateDirectory(_root);
        var store = new BookmarkStore(Path.Combine(_root, "e.db"));
        store.Toggle("https://github.com/", "GitHub já cá estava", DateTime.UtcNow);
        var added = BookmarkImport.Import(store, BookmarkImport.ParseChromium(ChromiumJson), DateTime.UtcNow);
        Assert.Equal(2, added);
        Assert.Equal(3, store.All().Count);
        Assert.Equal("GitHub já cá estava", store.All().Single(b => b.Url.Contains("github")).Title);
    }
}
