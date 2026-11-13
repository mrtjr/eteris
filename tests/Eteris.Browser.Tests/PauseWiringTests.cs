using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

// A Pausa (Ctrl+., MainWindow.Pause.cs) ligada ao resto da janela: a Pausa é um estado da janela que os pontos onde o
// Éteris decide o que se vê e o que se ouve consultam (sem isso, a próxima disposição, um site fixado que volta a
// carregar ou um motor que nasce durante a Pausa punham uma página à vista ou com som por baixo do ecrã calmo).
public class PauseWiringTests
{
    [Fact]
    public void CtrlPeriod_TogglesThePause_FromTheFrameAndFromThePage()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("if (ctrl && !shift && key == Key.OemPeriod) return TogglePause;", code);
        // A tecla vinda da página (fio do CEF) lê uma bandeira, não o WPF; com a Pausa ligada nenhuma tecla vai para a página.
        Assert.Matches(@"if \(_pausedForPage\)\s*\{", code);
        // Na moldura, a Pausa vem antes de tudo (Radar, menu, atalhos): Esc e Ctrl+. voltam, o resto não corre atalhos.
        Assert.Matches(@"AltKeyDown\(key, ctrl, shift\);\s*(//[^\n]*\n\s*)*if \(_pause is not null\)\s*\{\s*e\.Handled = HandlePauseKey\(key, ctrl, shift, alt, e\.IsRepeat\);\s*return;\s*\}\s*if \(RadarOpen\)", code);
    }

    [Fact]
    public void PaletteAndMenu_HaveThePause()
    {
        Assert.Contains("\"Pausa\", \"Silencia tudo e esconde as páginas — Esc para voltar\", \"Ctrl+.\", TogglePause", Source("MainWindow.Palette.cs"));
        Assert.Contains("\"Pausa\", TogglePause, \"Ctrl+.\"", Source("MainWindow.BrowserMenu.cs"));
    }

    [Fact]
    public void TheShortcutSheet_ListsThePause()
        => Assert.Contains(ShortcutSheet.Groups.SelectMany(g => g.Items), s => s.Keys == "Ctrl+." && s.Action.StartsWith("Pausa", StringComparison.Ordinal));

    // Nenhuma página à vista: a aba ativa, o 2.º painel do ecrã dividido e o motor de vídeo saem pela disposição normal
    // (ShowPages: minimizar a janela do Chromium e só depois esconder no WPF; ao voltar, o caminho da aba fria e o
    // StackPages); os sites fixados e a página segurada a meio de uma troca à parte.
    [Fact]
    public void Pages_AreHiddenThroughTheLayout()
    {
        Assert.Matches(@"private bool ShouldShow\(BrowserTab tab, FrameworkElement page\)\s*\{\s*// [^\n]*\n\s*if \(_pause is not null\) return false;", Source("MainWindow.Split.cs"));
        var webPanels = Source("MainWindow.WebPanels.cs");
        Assert.Contains("if (_pause is not null)", webPanels);
        Assert.Contains("SetPageVisible(browser, _sidebarPanel?.Url == url && _sidebarOpen && _pause is null);", webPanels);
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("HideWebPanels();", pause);
        Assert.Contains("LayoutBrowsers();", pause);
        Assert.Matches(@"foreach \(var held in _leavingPages\.Keys\.ToList\(\)\)", pause);
    }

    // O som: o silêncio da Pausa nunca passa pelo tab.IsMuted (o do utilizador); cada sítio que dá som a um motor respeita-o,
    // e um motor que nasce durante a Pausa entra calado. Um motor largado (aba fechada, adormecida, trocada de motor; site
    // fixado largado) sai do plano.
    [Fact]
    public void Mute_IsRespectedWhereverAnEngineGetsItsSound()
    {
        Assert.Contains("browser.GetBrowserHost()?.SetAudioMuted(tab.IsMuted || _pause is not null);", Source("MainWindow.Audio.cs"));
        var video = Source("MainWindow.VideoEngine.cs");
        Assert.Contains("core.IsMuted = tab.IsMuted || TestSwitches.Mute || _pause is not null;", video);
        Assert.Contains("if (tab.IsMuted || TestSwitches.Mute || _pause is not null) core.IsMuted = true;", video);
        Assert.Contains("JoinPause(view, tab.IsMuted);", video);
        Assert.Contains("Dispatcher.InvokeAsync(() => JoinPause(browser, tab.IsMuted));", Source("MainWindow.xaml.cs"));
        Assert.Contains("JoinPause(browser, muted: false);", Source("MainWindow.WebPanels.cs"));
        Assert.Matches(@"if \(!_browsers\.Remove\(tab, out var browser\)\) return;\s*_pause\?\.Plan\.Forget\(browser\);", Source("MainWindow.Sleep.cs"));
        Assert.Matches(@"if \(!_videoViews\.Remove\(tab, out var view\)\) return;\s*_pause\?\.Plan\.Forget\(view\);", video);
        Assert.Matches(@"if \(!_webPanelBrowsers\.Remove\(url, out var browser\)\) return;\s*_pause\?\.Plan\.Forget\(browser\);", Source("MainWindow.WebPanels.cs"));
    }

    // A camada por cima da página (barra de procura, permissões, avisos, janela flutuante) não se vê por cima do ecrã calmo;
    // os avisos ficam em fila e aparecem ao voltar; o foco nunca volta a uma página escondida.
    [Fact]
    public void Overlay_Toasts_AndFocus_WaitForTheReturn()
    {
        Assert.Matches(@"private bool OverlayNeeded\(\)\s*=> _pause is null && _overlayRoot is not null", Source("MainWindow.Overlay.cs"));
        Assert.Matches(@"private void ReturnFocusToPage\(\)\s*\{\s*(//[^\n]*\n\s*)*if \(_pause is not null\)\s*\{\s*FocusPauseView\(\);\s*return;\s*\}", Source("MainWindow.Overlay.cs"));
        Assert.Matches(@"private void ShowToast\(string message, params ToastAction\[\] actions\)\s*\{\s*(//[^\n]*\n\s*)*if \(_pause is not null\)\s*\{\s*QueuePausedToast\(message, actions\);\s*return;\s*\}", Source("MainWindow.Toast.cs"));
    }

    // Todas as janelas (normais, privadas e a flutuante) entram; uma janela aberta durante a Pausa não. Um link que chega de
    // outra aplicação acaba a Pausa só na janela que o recebe.
    [Fact]
    public void ThePause_RunsInEveryOpenWindow_AndALinkFromOutsideEndsItThere()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("Application.Current.Windows.OfType<MainWindow>()", pause);
        Assert.Matches(@"if \(url is not null \|\| StartupArgs\.WantsNewTab\(args\)\) target\.EndPauseHere\(\);", Source("MainWindow.Forward.cs"));
    }

    // A vista calma: nome de automação «Pausa», «Voltar» com o foco inicial, a contagem como região viva educada, o anel a
    // 30 fps e parado com «reduzir movimento», e em alto contraste as cores do sistema sem imagem.
    [Fact]
    public void TheCalmView_IsAccessible()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("AutomationProperties.SetName(view, PauseCapture.ViewName(capturing));", pause);
        Assert.Contains("AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Polite);", pause);
        Assert.Contains("Timeline.SetDesiredFrameRate(breath, 30);", pause);
        Assert.Contains("Motion.Reduced", pause);
        Assert.Contains("\"Respire devagar\"", pause);
        Assert.Contains("SystemParameters.HighContrast", pause);
        Assert.Contains("SystemColors.WindowBrush", pause);
        Assert.Contains("SystemColors.HighlightBrush", pause);
        Assert.Contains("\"Esc ou Ctrl+. para voltar\"", pause);
        Assert.Contains("Radius = 40", pause);
        // O fim do temporizador nunca acaba a Pausa nem toca som: nada do que o temporizador e o relógio correm (o refrescar,
        // com o tique da contagem, e o relógio) sai da Pausa, seja qual for a forma do código.
        foreach (var method in new[] { "RefreshPauseTimer", "TickPauseClock" })
        {
            var body = Regex.Match(pause, @"private void " + method + @"\(PauseState state\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
            Assert.NotEmpty(body);
            Assert.Contains(method == "RefreshPauseTimer" ? "PauseTimer.Ended(left)" : "clock.Text", body);
            Assert.DoesNotMatch(@"ResumeEverywhere|ExitPause|EndPause", body);
        }
        Assert.DoesNotContain("SystemSounds", pause);
    }

    // Ao voltar nada recomeça sozinho: o aviso «Retomar o que tocava (N)» corre o script com gesto do utilizador, como o
    // tocar/pausar da central de multimédia (no motor de vídeo, pelo canal único), dura 10 s, e a mesma ação fica na
    // paleta (o aviso só responde ao rato e ao leitor de ecrã).
    [Fact]
    public void Resume_IsOfferedNeverAutomatic_WithAUserGesture()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("PageScript.EvaluateAsync(browser, MediaInfo.ResumeMarkedScript, userGesture: true)", pause);
        Assert.Contains("VideoPageScript.EvaluateNumberAsync(core, MediaInfo.ResumeMarkedScript, userGesture: true)", pause);
        Assert.Contains("$\"Retomar o que tocava ({", pause);
        Assert.Contains("ToastPolicy.PauseReturnShown", pause);
        Assert.Equal(TimeSpan.FromSeconds(10), Eteris.Browser.Tabs.ToastPolicy.PauseReturnShown);
        // Só a quem o utilizador escolhe: o regresso nunca corre o script de retomar por conta própria.
        var exit = Regex.Match(pause, @"private void ExitPause\(\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.NotEmpty(exit);
        Assert.DoesNotContain("ResumeMarkedScript", exit);
        Assert.DoesNotContain("ReplayPaused()", exit);
        Assert.Contains("ReplayPaused", Source("MainWindow.Palette.cs"));
    }

    // Os botões dos avisos também respondem ao Invoke (leitor de ecrã, UI Automation), uma só vez.
    [Fact]
    public void ToastButtons_AlsoAnswerTheInvoke()
        => Assert.Matches(@"button\.Click \+= \(_, _\) =>\s*\{\s*if \(ran\) return;", Source("MainWindow.Toast.cs"));

    // Teclado: os botões do ecrã calmo recebem o foco (os PillButton do tema não o recebem) e mostram-no; só a janela ativa
    // recebe o teclado (as outras guardam o foco lógico: um Keyboard.Focus numa janela inativa podia ativá-la).
    [Fact]
    public void TheCalmView_TakesTheKeyboard_OnlyInTheActiveWindow()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("new Setter(UIElement.FocusableProperty, true)", pause);
        Assert.Contains("new Setter(FrameworkElement.FocusVisualStyleProperty,", pause);
        Assert.Matches(@"if \(IsActive\) Keyboard\.Focus\(back\);\s*else FocusManager\.SetFocusedElement\(this, back\);", pause);
        Assert.Matches(@"if \(IsActive\) ReturnFocusToPage\(\);\s*else if \(ActiveBrowser is \{ \} page\) FocusManager\.SetFocusedElement\(this, page\);", pause);
    }

    // O rato também: os botões laterais (voltar/avançar) não mexem na aba escondida. E a janela flutuante, que não tem
    // maximizar, também não o ganha no ecrã calmo.
    [Fact]
    public void SideMouseButtons_AndTheMiniWindow_RespectThePause()
    {
        Assert.Matches(@"private void OnSideMouseButton\(object sender, MouseButtonEventArgs e\)\s*\{\s*(//[^
]*
\s*)*if \(_pause is not null\) return;", Source("MainWindow.Mouse.cs"));
        Assert.Contains("maximize.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;", Source("MainWindow.Pause.cs"));
    }

    // O que a Pausa parou não adormece (a Pausa tirou-lhe o «a tocar»): nem por tempo, nem por pressão de memória, nem ao
    // mudar de Espaço (todos passam por IsHeld), nem o site fixado escondido. Depois de voltar, o que espera pelo
    // «Retomar» fica acordado o tempo de sono das abas a contar do regresso.
    [Fact]
    public void WhatThePausePaused_DoesNotFallAsleep()
    {
        var sleep = Source("MainWindow.Sleep.cs");
        Assert.Matches(@"private bool IsHeld\(BrowserTab tab, DateTime nowUtc\)\s*=> PauseHolds\(tab\) \|\|", sleep);
        Assert.Contains("if (!byUser && (IsCapturing(browser) || tab.Media == MediaState.Playing || PauseHolds(tab))) return;", sleep);
        var webPanels = Source("MainWindow.WebPanels.cs");
        Assert.Contains("if (PauseHolds(_webPanelBrowsers[url])) continue;", webPanels);
        Assert.Contains("|| (_sidebarOpen && _sidebarPanel?.Url == url) || IsCapturing(browser) || PauseHolds(browser)) return;", webPanels);
        var exit = Regex.Match(Source("MainWindow.Pause.cs"), @"private void ExitPause\(\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("HoldAfterPause(back.Replay);", exit);
    }

    // A Pausa não corta a captura (a chamada é do utilizador), mas o ecrã calmo tapa o indicador da barra de abas: diz quais
    // sites (abas e sites fixados) podem continuar a captar, à vista e no nome de automação.
    [Fact]
    public void TheCalmView_SaysWhoMayStillCapture()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Contains("var capturing = PauseCapture.Notice(CapturingSites());", pause);
        Assert.Matches(@"foreach \(var \(tab, browser\) in _browsers\)\s*if \(IsCapturing\(browser\)\)", pause);
        Assert.Matches(@"foreach \(var \(url, browser\) in _webPanelBrowsers\)\s*if \(IsCapturing\(browser\)\)", pause);
        Assert.Contains("Text = capturing,", pause);
    }

    // Ctrl+. (ou Esc) mantido premido não alterna a Pausa ao ritmo da repetição automática (cada nova Pausa apagava as
    // marcas da anterior e o «Retomar» ficava a não retomar nada): a repetição é engolida, venha a tecla da moldura ou da
    // página. Tab e as setas repetidas continuam a chegar aos chips.
    [Fact]
    public void AHeldPauseKey_DoesNotToggleAgain()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Matches(@"private bool HandlePauseKey\(Key key, bool ctrl, bool shift, bool alt, bool repeat\)\s*\{\s*if \(key == Key\.Escape \|\| IsPauseToggleKey\(key, ctrl, shift, alt\)\)\s*\{\s*(//[^\n]*\n\s*)*if \(!repeat\) ResumeEverywhere\(\);\s*return true;", pause);
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("else if (e.IsRepeat && IsPauseToggleKey(key, ctrl, shift, alt)) { }", code);
        Assert.Matches(@"if \(_pausedForPage\)\s*\{\s*if \(!repeat && \(key == Key\.Escape \|\| IsPauseToggleKey\(key, ctrl, shift, alt\)\)\) Dispatcher\.InvokeAsync\(ResumeEverywhere\);\s*return true;\s*\}", code);
        Assert.Contains("if (repeat && IsPauseToggleKey(key, ctrl, shift, alt)) return true;", code);
    }

    // O temporizador é partilhado: o Ctrl+. numa janela que não está em Pausa (aberta durante ela, ou onde um link de fora a
    // acabou) recomeça-o, e as janelas já em Pausa mostram o mesmo (sem isto ficavam com «Pronto» e o anel parado).
    [Fact]
    public void ANewPause_RefreshesTheWindowsAlreadyPaused()
    {
        var everywhere = Regex.Match(Source("MainWindow.Pause.cs"), @"private static void PauseEverywhere\(\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.NotEmpty(everywhere);
        Assert.Contains("foreach (var window in PausedWindows()) window.RefreshPauseTimer(window._pause!);", everywhere);
    }

    // Uma transferência que começa durante a Pausa (uma página escondida, a resposta a um clique de antes do Ctrl+.) não
    // abre o painel das transferências por cima do ecrã calmo (um Popup é uma janela própria): abre ao voltar.
    [Fact]
    public void ADownloadDuringThePause_OpensItsPanelOnTheReturn()
    {
        Assert.Matches(@"if \(_pause is \{ \} paused\) paused\.DownloadStarted = true;\s*else DownloadsPopup\.IsOpen = true;", Source("MainWindow.Downloads.cs"));
        var exit = Regex.Match(Source("MainWindow.Pause.cs"), @"private void ExitPause\(\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("if (state.DownloadStarted) DownloadsPopup.IsOpen = true;", exit);
    }

    // O pedido de certificado de cliente de uma página escondida pela Pausa (a aba ativa, o site fixado aberto) espera, como
    // o de uma aba fora da vista: o diálogo modal não se abre por cima do ecrã calmo (nem leva o Esc de quem quer voltar);
    // aparece ao voltar.
    [Fact]
    public void AClientCertificateRequest_WaitsForTheReturn()
    {
        Assert.Contains("() => _pause is null && Tabs.Tabs.Contains(tab) && IsTabOnScreen(tab)", Source("MainWindow.xaml.cs"));
        Assert.Contains("() => _pause is null && _sidebarOpen && _sidebarPanel?.Url == url", Source("MainWindow.WebPanels.cs"));
        var exit = Regex.Match(Source("MainWindow.Pause.cs"), @"private void ExitPause\(\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("ShowNextClientCertificate();", exit);
    }

    // O «Retomar» guarda motores, não documentos: quando a página principal navega (o utilizador levou a aba para outro
    // site), as marcas foram-se e o motor sai do «Retomar» (e do plano da Pausa, se ela ainda dura) — o comando da paleta não
    // dá o gesto do utilizador ao documento novo.
    [Fact]
    public void ANavigation_TakesTheEngineOutOfTheResume()
    {
        Assert.Contains("Dispatcher.InvokeAsync(() => ForgetReplay(_browsers.GetValueOrDefault(tab)));", Source("MainWindow.xaml.cs"));
        Assert.Contains("Dispatcher.InvokeAsync(() => ForgetReplay(_webPanelBrowsers.GetValueOrDefault(url)));", Source("MainWindow.WebPanels.cs"));
        Assert.Matches(@"private void ForgetReplay\(FrameworkElement\? engine\)\s*\{\s*if \(engine is null\) return;\s*_replay\.Remove\(engine\);\s*_pause\?\.Plan\.Navigated\(engine\);\s*\}", Source("MainWindow.Pause.cs"));
    }

    // A aba que o utilizador já tinha silenciado não é pausada pela Pausa (só sai do Picture-in-Picture): ficava parada para
    // sempre, fora do «Retomar».
    [Fact]
    public void AMutedTab_IsNotPausedByThePause()
        => Assert.Contains("var script = state.Plan.WasMuted(engine) ? MediaInfo.LeavePictureInPictureScript : MediaInfo.PauseMarkScript;", Source("MainWindow.Pause.cs"));

    // As teclas próprias do site fixado (F5, Ctrl+F, Ctrl+P, F12) passam pela mesma barreira da Pausa que as outras: com o
    // foco do Win32 num site fixado escondido, imprimiam, abriam as ferramentas ou recarregavam por baixo do ecrã calmo.
    [Fact]
    public void ThePinnedSiteKeys_StopAtThePause()
        => Assert.Matches(@"private bool ShortcutFromWebPanel\(ChromiumWebBrowser browser, int virtualKey, bool ctrl, bool shift, bool alt, bool repeat\)\s*\{\s*(//[^\n]*\n\s*)*if \(_pausedForPage\) return ShortcutFromPage\(virtualKey, ctrl, shift, alt, repeat: repeat\);", Source("MainWindow.WebPanels.cs"));

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.sln")) && !Directory.Exists(Path.Combine(dir.FullName, "src", "Eteris.Browser")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Source(string relative)
    {
        var path = Path.Combine(Root, "src", "Eteris.Browser", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
