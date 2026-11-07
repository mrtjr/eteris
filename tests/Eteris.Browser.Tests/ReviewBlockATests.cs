using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sidebar;
using Eteris.Browser.Tabs;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A: janelas fechadas com Espaços, sites fixados partilhados entre janelas, o
// Espreitar só com um Alt verdadeiro e a imagem do cartão pedida pelo motor.
public class ReviewBlockATests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-rev-" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (Exception) { }
    }

    [Fact]
    public void ClosedWindow_WithTabsOnlyInOtherSpaces_IsKept()
    {
        var work = new SessionSpace("Trabalho", 2, null, new Session(["https://a.pt/", "https://b.pt/"], 0));
        var closed = new ClosedWindows();
        closed.Push(new ClosedWindow([], [], 0, [new SessionSpace("Geral", 0), work], 0));
        Assert.Equal(1, closed.Count);
        var back = closed.Pop()!;
        Assert.Equal(["https://a.pt/", "https://b.pt/"], back.Spaces![1].Tabs!.Urls);
        // Sem abas em lado nenhum continua a não contar.
        closed.Push(new ClosedWindow([], [], 0, [new SessionSpace("Geral", 0), new SessionSpace("Vazio", 1, null, new Session([], 0))], 0));
        Assert.Equal(0, closed.Count);
    }

    [Fact]
    public void ClosingAWindow_KeepsTheTabsOfEverySpace()
    {
        var tabs = new TabsViewModel();
        tabs.NewTab("https://geral.pt/");
        tabs.NewWorkspace("Trabalho", newTabAddress: "https://trabalho.pt/");
        tabs.NewTab("https://trabalho2.pt/");
        tabs.SwitchWorkspace(tabs.Workspaces[0]);
        var (spaces, index) = tabs.WorkspaceSnapshot();
        var window = new ClosedWindow(tabs.Tabs.Select(t => t.Address).ToList(), tabs.Tabs.Select(t => t.Title).ToList(), 0, spaces, index);

        var reopened = new TabsViewModel();
        foreach (var url in window.Addresses) reopened.NewTab(url);
        reopened.RestoreWorkspaces(window.Spaces, window.SpaceIndex);
        Assert.Equal(2, reopened.Workspaces.Count);
        Assert.Equal(["https://trabalho.pt/", "https://trabalho2.pt/"], reopened.TabsOf(reopened.Workspaces[1]).Select(t => t.Address));
    }

    [Fact]
    public void WebPanelStore_UpdateStartsFromTheFile_SoTwoWindowsDoNotEraseEachOther()
    {
        var store = new WebPanelStore(_path);
        // Janela B leu a lista (vazia) antes de A fixar; ao fixar parte do ficheiro, não da sua cópia.
        var staleInB = store.Load();
        store.Update(list => WebPanels.Add(list, "https://chat.exemplo/", "Chat"));
        store.Update(list => WebPanels.Add(list, "https://mail.exemplo/", "Mail"));
        Assert.Empty(staleInB);
        Assert.Equal(["https://chat.exemplo/", "https://mail.exemplo/"], store.Load().Select(p => p.Url));
        // Nada a mudar: o ficheiro fica como estava.
        Assert.Null(store.Update(_ => null));
        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void WebPanelStore_UnreadableFile_LoadsEmpty()
    {
        File.WriteAllText(_path, "[]");
        var info = new FileInfo(_path);
        var security = info.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(_path));
            Assert.Empty(new WebPanelStore(_path).Load());
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void Glance_NeedsARealAltPress_OneCardPerPress()
    {
        var tap = new AltTap();
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        // A página escreve o aviso sem ninguém ter tocado no Alt.
        Assert.False(tap.TakeGesture(now, LinkPreview.GestureWindow));
        tap.KeyDown(isAlt: true, otherModifiers: false, now);
        Assert.True(tap.TakeGesture(now.AddMilliseconds(400), LinkPreview.GestureWindow));
        // Repetir o aviso (setInterval) com o mesmo Alt já não abre outro cartão.
        Assert.False(tap.TakeGesture(now.AddMilliseconds(900), LinkPreview.GestureWindow));
        tap.KeyUp(isAlt: true);
        // Um Alt antigo não serve; Alt+← (outra tecla) também não.
        tap.KeyDown(isAlt: true, otherModifiers: false, now);
        Assert.False(tap.TakeGesture(now + LinkPreview.GestureWindow + TimeSpan.FromSeconds(1), LinkPreview.GestureWindow));
        tap.KeyUp(isAlt: true);
        tap.KeyDown(isAlt: true, otherModifiers: false, now);
        tap.KeyDown(isAlt: false, otherModifiers: false, now);
        Assert.False(tap.TakeGesture(now, LinkPreview.GestureWindow));
    }

    [Theory]
    [InlineData(200, "image/jpeg", 1000, true)]
    [InlineData(200, null, 1000, true)]
    [InlineData(404, "image/png", 1000, false)]
    [InlineData(200, "text/html", 1000, false)]
    [InlineData(200, "image/png", 0, false)]
    [InlineData(200, "image/png", LinkPreview.MaxImageBytes + 1, false)]
    public void GlanceImage_AcceptsOnlySuccessfulImages(int status, string? mime, long length, bool expected)
        => Assert.Equal(expected, LinkPreview.AcceptsImage(status, mime, length));

    [Fact]
    public void GlanceImage_IsFetchedByTheEngine_NeverByHttpClient()
    {
        var root = FindRoot();
        var glance = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.Glance.cs"));
        Assert.DoesNotContain("HttpClient", glance);
        Assert.Contains("CreateUrlRequest", glance);
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
