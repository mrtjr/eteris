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
    // omissão, já sem esperar por nada. A vigia nasce depois da 1.ª página (o construtor da janela só a deixa marcada),
    // nenhuma página se esconde antes de apresentar e não mexe no motor: fica fora da rede de segurança do arranque e
    // do grupo «motor».
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

    // O relógio que vê a área de trabalho (o DWM não avisa quando destapa a janela). Parado com a janela ativa e à
    // vista, e com ela minimizada; a andar com ela à vista e não ativa, mesmo acabada de restaurar sem ativar. E, escondida
    // por estar noutra área, anda até a janela se voltar a ver, mesmo ativa: clicar no ícone do Éteris a partir de outra
    // área (ou o Alt+Tab) ativa a janela antes de o Windows a destapar, e sem o relógio a página ficava em branco.
    [Theory]
    [InlineData(true, false, UnseenWindow.Reason.None, false)]
    [InlineData(false, false, UnseenWindow.Reason.None, true)]
    [InlineData(true, true, UnseenWindow.Reason.Minimized, false)]
    [InlineData(false, true, UnseenWindow.Reason.Minimized, false)]
    [InlineData(true, false, UnseenWindow.Reason.OtherDesktop, true)]
    [InlineData(false, false, UnseenWindow.Reason.OtherDesktop, true)]
    [InlineData(true, false, UnseenWindow.Reason.Locked, false)]
    [InlineData(false, false, UnseenWindow.Reason.Locked, true)]
    public void Watch_RunsUntilTheWindowIsSeenAgain(bool active, bool minimized, UnseenWindow.Reason reason, bool expected)
        => Assert.Equal(expected, UnseenWindow.Watches(active, minimized, reason));

    // De segundo a segundo à espera de a janela ir para outra área; quatro vezes por segundo à espera de ela voltar (é o
    // tempo que a página fica por desenhar numa janela que já se vê).
    [Fact]
    public void Watch_IsFasterWhileWaitingForTheWindowToComeBack()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), UnseenWindow.WatchEvery(UnseenWindow.Reason.OtherDesktop));
        foreach (var reason in new[] { UnseenWindow.Reason.None, UnseenWindow.Reason.Minimized, UnseenWindow.Reason.Locked })
            Assert.Equal(TimeSpan.FromSeconds(1), UnseenWindow.WatchEvery(reason));
    }

    // A janela segue a regra em todos os sítios onde o estado muda: ao ativar (revê e só pára se a regra disser), ao
    // desativar, ao mudar de estado (o OnWindowStateChanged chama o ReviewUnseen: minimizar pára, restaurar sem ativar
    // recomeça) e em cada tique. Nenhum deles pára o relógio por conta própria.
    [Fact]
    public void Window_FollowsTheWatchRule_OnActivationDeactivationStateAndTick()
    {
        var unseen = Source("MainWindow.Unseen.cs");
        Assert.Contains("UnseenWindow.Watches(IsActive, WindowState == WindowState.Minimized, _unseenReason)", unseen);
        Assert.Contains("UnseenWindow.WatchEvery(_unseenReason)", unseen);
        Assert.Contains("Activated += (_, _) => ReviewUnseen();", unseen);
        Assert.Contains("Deactivated += (_, _) => WatchCloak();", unseen);
        Assert.Contains("_cloakTimer.Tick += (_, _) => ReviewUnseen();", unseen);
        // O ReviewUnseen acerta o relógio no fim, com o motivo já atualizado, mude ele ou não.
        Assert.Matches(@"else HideUnseenPages\(\);\s*\}\s*WatchCloak\(\);\s*\}", unseen);
        // Só a regra (e o fecho da janela) param o relógio.
        Assert.Equal(2, Regex.Matches(unseen, @"\.Stop\(\);").Count);
        Assert.DoesNotContain("_cloakTimer.Stop();\n            ReviewUnseen();", unseen.Replace("\r\n", "\n"));
    }

    // Quando a janela se volta a ver, voltam todas as páginas escondidas que continuam visíveis no WPF, não só a da aba
    // ativa: a que ficou segurada por cima da que entra (um link aberto por outra aplicação com o Éteris minimizado abre
    // a aba antes de a janela voltar, e a página antiga é a que se vê até a nova apresentar) e a que ficou quente (volta
    // sem esperar por fotograma nenhum). Minimizada, qualquer delas era uma janela vazia. Um site fixado só volta se for
    // o que está à vista na barra lateral.
    [Fact]
    public void Restore_BringsBackEveryPageStillVisible_NotOnlyTheActiveTab()
    {
        var restore = Method(Source("MainWindow.Unseen.cs"), "private void RestoreUnseenPage(ChromiumWebBrowser page)");
        Assert.Contains("if (page.IsDisposed || page.Visibility != Visibility.Visible) return;", restore);
        Assert.DoesNotContain("ShouldShow", restore);
        Assert.Contains("if (_webPanelBrowsers.ContainsValue(page) && !(_sidebarOpen && ReferenceEquals(page, CurrentWebPanelBrowser))) return;", restore);
        Assert.Contains("ShowPageWindow(page, ArrivalPixels(page));", restore);
    }

    // Uma página que ainda não apresentou o 1.º fotograma não se esconde: escondida, o rAF não resolve, o arranque não é
    // dado por bom (StartupGuard.Healthy) e uma queda dessa sessão, mesmo horas depois, contava contra o sandbox como
    // «queda antes da 1.ª página». Esconde-se quando apresentar (depois do Healthy), se a janela continuar sem se ver.
    [Fact]
    public void PageThatHasNotPresented_IsNotHidden_UntilItPresents()
    {
        var hide = Method(Source("MainWindow.Unseen.cs"), "private void HideUnseenPages()");
        var skip = hide.IndexOf("if (!_presentation.HasPresented(browser)) continue;", StringComparison.Ordinal);
        Assert.True(skip > 0 && skip < hide.IndexOf("SetPageVisible(browser, false);", StringComparison.Ordinal), "a página por apresentar salta antes de ser escondida");
        var pages = Source("MainWindow.PageVisibility.cs");
        var wait = Method(pages, "private async Task WaitPresentedAsync(");
        var healthy = wait.IndexOf("Infrastructure.StartupGuard.Current.Healthy();", StringComparison.Ordinal);
        var keep = wait.IndexOf("KeepUnseenHidden();", StringComparison.Ordinal);
        Assert.True(healthy > 0 && keep > healthy, "esconde-se depois de o arranque ser dado por bom");
        Assert.Matches(@"_presentation\.Presented\(page, arrival\);\s*Infrastructure\.StartupGuard\.Current\.Healthy\(\);\s*(//[^\n]*\n\s*)*KeepUnseenHidden\(\);",
            Method(pages, "private async Task ConfirmPresentedAsync("));
    }

    // O e8 não põe trabalho no arranque: o construtor da janela só deixa a vigia marcada para depois da 1.ª página
    // (FirstPaint), e é aí que nascem a subscrição do SessionSwitch e o relógio da área de trabalho. O que corre no
    // construtor é só a ordem do minimizar (o ReviewUnseen do OnWindowStateChanged), que não esconde nada antes de a
    // página apresentar. Uma janela fechada antes disso não subscreve nada.
    [Fact]
    public void Watch_StartsAfterTheFirstPage_NotInTheWindowConstructor()
    {
        var watch = Method(Source("MainWindow.Unseen.cs"), "private void WatchUnseen()");
        var then = watch.IndexOf("FirstPaint.App.Then(", StringComparison.Ordinal);
        Assert.True(then > 0, "a vigia do e8 espera pela 1.ª página");
        Assert.True(watch.IndexOf("SystemEvents.SessionSwitch += OnSession;", StringComparison.Ordinal) > then);
        Assert.True(watch.IndexOf("new DispatcherTimer(", StringComparison.Ordinal) > then);
        Assert.Matches(@"if \(closed\) return;", watch);
        // Os comentários já não dizem que o e8 «corre depois» sem mais: dizem o que fica no construtor.
        var tuning = Source(Path.Combine("Infrastructure", "PerfTuning.cs"));
        Assert.Contains("o e8 só deixa a vigia marcada no construtor da janela", tuning);
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

    // O texto de um método (da assinatura até à chaveta que o fecha, com a indentação dos membros da classe).
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"sem «{signature}»");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"«{signature}» sem fim");
        return source[start..(end + 6)];
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
