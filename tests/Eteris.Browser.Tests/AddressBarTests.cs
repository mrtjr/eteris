using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class AddressBarTests
{
    [Fact]
    public void Pending_ShowsTypedTextWithoutPadlock()
    {
        Assert.Equal("https://banco.example/", AddressBar.Text("https://outro.example/", "https://banco.example/"));
        Assert.Equal(ConnectionKind.Unknown, AddressBar.Icon("https://outro.example/", "https://banco.example/", false, false, false, false));
    }

    [Fact]
    public void LoadEndedWithoutCommit_RevertsToCommitted()
    {
        Assert.True(AddressBar.ClearsPending(isLoading: false, loadStarted: true));
        Assert.False(AddressBar.ClearsPending(isLoading: true, loadStarted: true));
        Assert.False(AddressBar.ClearsPending(isLoading: false, loadStarted: false));   // fim de carga da página anterior
        Assert.Equal("https://outro.example/", AddressBar.Text("https://outro.example/", null));
        Assert.Equal(ConnectionKind.Secure, AddressBar.Icon("https://outro.example/", null, false, false, false, false));
    }

    [Fact]
    public void EditedText_HidesTheLock()
        => Assert.Equal(ConnectionKind.Unknown, AddressBar.Icon("https://outro.example/", null, showsError: false, edited: true, focused: false, empty: false));

    [Fact]
    public void ErrorPage_AndFocus()
    {
        Assert.Equal(ConnectionKind.Unknown, AddressBar.Icon("https://x.pt/", null, showsError: true, false, false, false));
        Assert.Null(AddressBar.Icon("https://x.pt/", null, false, false, focused: true, empty: false));
        Assert.Null(AddressBar.Icon(null, null, false, false, focused: false, empty: true));
        Assert.Equal(ConnectionKind.Insecure, AddressBar.Icon("http://x.pt/", null, false, false, false, false));
    }

    [Fact]
    public void TypedAddress_NeverBecomesTheTabAddressBeforeCommit()
    {
        string Source(string file) => File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file));
        var omnibox = Source("MainWindow.Omnibox.cs");
        Assert.Contains("tab.PendingAddress = url;", omnibox);
        Assert.DoesNotContain("tab.Address = url;", omnibox);
        Assert.Contains("AddressBar.Icon(", omnibox);
        Assert.Contains("AddressBar.ClearsPending(", Source("MainWindow.xaml.cs"));
        // O motor de vídeo: uma navegação pedida numa aba que já lá está segue a mesma regra.
        var video = Source("MainWindow.VideoEngine.cs");
        var load = video[video.IndexOf("private bool LoadInVideoView", StringComparison.Ordinal)..];
        load = load[..load.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("tab.PendingAddress = url;", load);
        Assert.DoesNotContain("tab.Address = url;", load);
    }
}
