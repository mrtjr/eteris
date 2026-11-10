using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// E8 do plano de desempenho da 0.2.14 (ajuste e8, ligado por omissão desde a medição D4): com a janela minimizada,
// noutra área de trabalho ou com a sessão bloqueada, a página à vista também se esconde — nunca com som, multimédia,
// captura ou Picture-in-Picture. A janela tapada por outra fica de fora (o e8 não a resolve).
public class UnseenWindowTests
{
    private static readonly string[] Exe = [@"C:\x\Eteris.Browser.exe"];

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

    // O diagnóstico D4 mediu-se (página animada, janela minimizada: 6,47% → 0,59% de um núcleo com o e8): ligado por
    // omissão, já sem esperar por nada. Corre depois da 1.ª página e não mexe no motor: fica fora da rede de segurança
    // do arranque e do grupo «motor».
    [Fact]
    public void Tweak_IsOnByDefault_SinceTheDiagnostic()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.HideWhenUnseen);
        Assert.True(tweak.OnByDefault);
        Assert.Null(tweak.Waits);
        Assert.False(tweak.BeforeFirstPaint);
        Assert.False(tweak.Engine);
        Assert.Contains(PerfTuning.HideWhenUnseen, PerfTuning.Resolve(Exe, testValue: null));
    }

    // Para os braços A/B das medições (e para o recuo): desliga-se sozinho pela variável de teste ou pelo argumento,
    // sem levar os outros atrás, e com todos pelo --eteris-sem-ajustes.
    [Fact]
    public void Tweak_TurnsOffByTheSwitches()
    {
        var others = PerfTuning.Defaults.Where(id => id != PerfTuning.HideWhenUnseen).Order().ToList();
        Assert.Equal(others, PerfTuning.Resolve(Exe, "-e8").Order());
        Assert.Equal(others, PerfTuning.Resolve([.. Exe, "--eteris-ajustes=-e8"], null).Order());
        Assert.Empty(PerfTuning.Resolve([.. Exe, "--eteris-sem-ajustes"], null));
    }

    // O e8 não resolve a janela tapada por outra (medido: continua a desenhar): não há esse motivo na regra, e a
    // descrição do ajuste não o promete.
    [Fact]
    public void CoveredWindow_IsNotPromised()
    {
        Assert.Equal(["None", "Minimized", "OtherDesktop", "Locked"], Enum.GetNames<UnseenWindow.Reason>());
        var what = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.HideWhenUnseen).What;
        Assert.DoesNotMatch("(?i)cobert|tapad|atrás de", what);
        Assert.Contains("minimizada", what);
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

    // Com a câmara, o microfone ou o ecrã em uso a página nunca se esconde: nem a da aba, nem o site fixado à vista na
    // barra lateral; e quando não se consegue saber (o motor a fechar), conta como em uso.
    [Fact]
    public void Capture_KeepsEveryPageVisible_AndAnUnknownStateCountsAsCapturing()
    {
        var unseen = Source("MainWindow.Unseen.cs");
        Assert.Contains("!_unseenPages.Contains(panel) && !IsCapturing(panel))", unseen);
        Assert.Matches(@"private static bool IsCapturing\(ChromiumWebBrowser browser\)\s*\{\s*"
            + @"try \{ return [^\n]*CaptureSessions\.IsCapturing\(core\.Identifier\); \}\s*catch \(Exception\) \{ return true; \}", unseen);
        // As duas únicas chamadas que escondem uma página por a janela não se ver vêm depois da pergunta pela captura.
        Assert.Equal(2, Regex.Matches(unseen, @"SetPageVisible\(\w+, false\);").Count);
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
