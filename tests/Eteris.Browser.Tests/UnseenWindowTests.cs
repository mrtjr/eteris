using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// E8 do plano de desempenho da 0.2.14 (ajuste e8, à espera do D4): com a janela que não se vê, a página à vista também
// se esconde — nunca com som, multimédia, captura ou Picture-in-Picture.
public class UnseenWindowTests
{
    [Theory]
    [InlineData(false, false, false, UnseenWindow.Reason.None)]
    [InlineData(true, false, false, UnseenWindow.Reason.Minimized)]
    [InlineData(false, true, false, UnseenWindow.Reason.OtherDesktop)]
    [InlineData(false, false, true, UnseenWindow.Reason.Locked)]
    [InlineData(true, true, true, UnseenWindow.Reason.Minimized)]
    [InlineData(false, true, true, UnseenWindow.Reason.Locked)]
    public void Why_TheWindowIsNotSeen(bool minimized, bool cloaked, bool locked, UnseenWindow.Reason expected)
        => Assert.Equal(expected, UnseenWindow.Why(minimized, cloaked, locked));

    [Fact]
    public void QuietPage_MayHide()
        => Assert.True(UnseenWindow.MayHide(audible: false, MediaState.None, capturing: false, pictureInPicture: false));

    [Theory]
    [InlineData(true, MediaState.None, false, false)]
    [InlineData(false, MediaState.Playing, false, false)]
    [InlineData(false, MediaState.Paused, false, false)]
    [InlineData(false, MediaState.None, true, false)]
    [InlineData(false, MediaState.None, false, true)]
    public void SoundMediaCaptureOrPip_StayVisible(bool audible, MediaState media, bool capturing, bool pip)
        => Assert.False(UnseenWindow.MayHide(audible, media, capturing, pip));

    [Fact]
    public void Capture_LastsUntilTheDocumentChanges()
    {
        CaptureSessions.Started(424242);
        Assert.True(CaptureSessions.IsCapturing(424242));
        CaptureSessions.Ended(424242);
        Assert.False(CaptureSessions.IsCapturing(424242));
    }

    [Fact]
    public void Tweak_IsOffUntilTheDiagnostic()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.HideWhenUnseen);
        Assert.False(tweak.OnByDefault);
        Assert.Equal("D4", tweak.Waits);
    }

    [Fact]
    public void PictureInPictureScript_LooksAtVideoAndDocument()
    {
        Assert.Contains("document.pictureInPictureElement", UnseenWindow.PictureInPictureScript);
        Assert.Contains("documentPictureInPicture.window", UnseenWindow.PictureInPictureScript);
    }

    // Ordem do T5 (docs/motor.md): o StateChanged da janela esconde a página antes de o CefSharp a encolher (o primeiro
    // passo do OnWindowStateChanged, assinado no construtor, antes de nascer qualquer browser); ao restaurar, as páginas
    // voltam depois de o CefSharp lhes devolver o tamanho.
    [Fact]
    public void Window_HidesBeforeCefSharpShrinksAndRestoresAfter()
    {
        var window = Source("MainWindow.xaml.cs");
        Assert.Matches(@"private void OnWindowStateChanged\(\)\s*\{\s*//[^\n]*\n\s*ReviewUnseen\(\);", window);
        Assert.Contains("WatchUnseen();", window);
        var unseen = Source("MainWindow.Unseen.cs");
        Assert.Matches(@"foreach \(var page in pages\) RestoreUnseenPage\(page\);\s*\}, DispatcherPriority\.Loaded\);", unseen);
        Assert.Contains("ShowPageWindow(page, ArrivalPixels(page));", unseen);
        Assert.Contains("SystemEvents.SessionSwitch -= OnSession;", unseen);
        Assert.Contains("UnseenWindow.MayHide(tab.IsAudible, tab.Media, IsCapturing(browser), pictureInPicture: false)", unseen);
    }

    [Fact]
    public void Capture_IsRecordedWhenAllowedAndForgottenOnANewDocument()
    {
        Assert.Contains("if (allowed) Tabs.CaptureSessions.Started(browserId);", Source(Path.Combine("Permissions", "SitePermissionHandler.cs")));
        Assert.Matches(@"if \(!e\.Frame\.IsMain\) return;\s*//[^\n]*\n\s*CaptureSessions\.Ended\(e\.Browser\.Identifier\);", Source("MainWindow.xaml.cs"));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
