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

    // Prova ao vivo do ciclo 5: depois de um endereço escrito na barra (ou de «voltar a casa»), a barra ficava com ⓘ em vez
    // do cadeado. O AddressChanged escrevia tab.Address (que refresca a barra) com o pedido ainda pendente
    // (AddressBar.Icon → Unknown) e só depois o limpava, sem voltar a refrescar; o fim da carga já não o fazia, porque o
    // PendingLoadStarted também tinha sido limpo.
    [Fact]
    public void EngineCommit_ClearsThePendingBeforeTheAddress_AndRefreshesTheBar()
    {
        var code = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.xaml.cs"));
        var block = code[code.IndexOf("browser.AddressChanged += () =>", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("browser.ConsoleMessage +=", StringComparison.Ordinal)];
        var pending = block.IndexOf("tab.PendingAddress = null;", StringComparison.Ordinal);
        var started = block.IndexOf("tab.PendingLoadStarted = false;", StringComparison.Ordinal);
        var address = block.IndexOf("tab.Address = address;", StringComparison.Ordinal);
        Assert.True(pending >= 0 && started >= 0 && address >= 0);
        Assert.True(pending < address, "o pendente tem de sair antes de o endereço refrescar a barra");
        Assert.True(started < address);
        // O mesmo endereço outra vez (recarregar o que já estava aberto) não muda Address: a barra refresca-se à mesma.
        Assert.Matches(@"tab\.Address = address;\s*(//[^\n]*\s*)*if \(wasPending && ReferenceEquals\(tab, Tabs\.Active\)\) SyncOmnibox\(\);", block);
    }

    [Fact]
    public void VideoEngine_ErrorPage_HasNoPadlock()
    {
        // A página de erro do próprio WebView2 (sem rede, nome que não existe) mostra o endereço que falhou: o cadeado e
        // «Ligação segura» não podem aparecer. O WebView2 di-lo no ContentLoading (IsErrorPage), depois do SourceChanged.
        var video = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.VideoEngine.cs"));
        var loading = video[video.IndexOf("core.ContentLoading +=", StringComparison.Ordinal)..];
        loading = loading[..loading.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("tab.ShowsErrorPage = e.IsErrorPage;", loading);
        Assert.Contains("UpdateOmniboxChrome()", loading);
    }

    [Fact]
    public void TypedAddress_IsDropped_WhenTheEngineGoesAwayBeforeLoading()
    {
        // O motor saiu enquanto os scripts do site se registavam (a aba adormeceu, mudou de motor): o endereço escrito não
        // fica preso na barra como se ainda estivesse a carregar.
        var omnibox = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.Omnibox.cs"));
        Assert.Matches(@"await ScriptsReadyFor\(tab, browser, url\);\s*(//[^\n]*\s*)?if \(browser\.IsDisposed\)\s*\{\s*tab\.PendingAddress = null;", omnibox);
    }

    [Fact]
    public void VideoEngine_EveryAddressWrite_ClearsTheErrorPageMark()
    {
        // Passar da página de erro para o motor de vídeo (ou de volta) não deixa a marca de «página de erro» agarrada.
        var lines = File.ReadAllLines(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.VideoEngine.cs"));
        var writes = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("tab.Address = ", StringComparison.Ordinal)) continue;
            writes++;
            Assert.Contains(lines.Skip(i).Take(4), l => l.Contains("tab.ShowsErrorPage = false;", StringComparison.Ordinal));
        }
        Assert.Equal(4, writes);
    }
}
