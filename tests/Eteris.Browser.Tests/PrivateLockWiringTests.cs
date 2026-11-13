using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// A tranca das janelas privadas (MainWindow.PrivateLock.cs) ligada ao resto da janela: como a Pausa, é um estado da janela
// (_privateLock) que os pontos onde o Éteris decide o que se vê, o que se ouve e para onde vão as teclas consultam. Trancada,
// nada da página se vê nem se lê: nem a página, nem o título, nem a árvore de automação, nem a miniatura, nem um aviso.
public class PrivateLockWiringTests
{
    private static string Body(string code, string signature)
    {
        var match = Regex.Match(code, Regex.Escape(signature) + @"[^\n]*\n    \{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, "não encontrei " + signature);
        return match.Groups["body"].Value;
    }

    // Trancada, nenhuma tecla vai para a página escondida e nenhum atalho do Éteris corre: só o cartão recebe teclas, e o
    // Ctrl+. continua a ser a Pausa. A tranca vem antes de tudo, também da Pausa.
    [Fact]
    public void TheLock_ComesFirstForTheKeys()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Matches(@"e\.Handled = true;\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\)\s*\{\s*e\.Handled = HandlePrivateLockKey\(key, ctrl, shift, alt, e\.IsRepeat\);\s*return;\s*\}\s*AltKeyDown\(key, ctrl, shift\);", code);
        var fromPage = Body(code, "private bool ShortcutFromPage(");
        var locked = fromPage.IndexOf("if (_lockedForPage)", StringComparison.Ordinal);
        Assert.True(locked > 0 && locked < fromPage.IndexOf("if (_pausedForPage)", StringComparison.Ordinal), "a tranca antes da Pausa");
        Assert.Matches(@"if \(_lockedForPage\)\s*\{\s*if \(!repeat && IsPauseToggleKey\(key, ctrl, shift, alt\)\) Dispatcher\.InvokeAsync\(TogglePause\);\s*return true;\s*\}", fromPage);
        Assert.Contains("if (_lockedForPage) return ShortcutFromPage(", Source("MainWindow.WebPanels.cs"));
        var keys = Body(Source("MainWindow.PrivateLock.cs"), "private bool HandlePrivateLockKey(");
        Assert.Contains("IsPauseToggleKey(key, ctrl, shift, alt)", keys);
        Assert.Contains("TogglePause();", keys);
        Assert.Matches(@"if \(_pause is not null\) return;\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\) return;", Source("MainWindow.Mouse.cs"));
    }

    // Ctrl+Alt+L, só com o Alt da esquerda (o AltGr+L escreve «ł») e só com uma janela privada aberta (lido de um contador,
    // porque o Shortcut também corre no fio do CEF): sem janela privada, continua da página. Antes dos Espaços.
    [Fact]
    public void CtrlAltL_LocksThePrivateWindows_OnlyWithTheLeftAltAndAPrivateWindowOpen()
    {
        var shortcut = Body(Source("MainWindow.xaml.cs"), "private Action? Shortcut(Key key, bool ctrl, bool shift, bool alt)");
        var block = Regex.Match(shortcut, @"if \(ctrl && alt\)\s*\{(?<block>.*?)\n        \}", RegexOptions.Singleline).Groups["block"].Value;
        var lockKey = block.IndexOf("if (PrivateLockPolicy.IsLockKey(key, shift, IsLeftAltDown()) && s_privateWindowsOpen > 0) return LockPrivateWindowsNow;", StringComparison.Ordinal);
        Assert.True(lockKey >= 0, "o Ctrl+Alt+L no bloco do Ctrl+Alt");
        Assert.True(lockKey < block.IndexOf("WorkspaceShortcut.Resolve", StringComparison.Ordinal));
        Assert.Contains("s_privateWindowsOpen++;", Source("MainWindow.PrivateLock.cs"));
        Assert.Contains("s_privateWindowsOpen--;", Source("MainWindow.PrivateLock.cs"));
    }

    [Fact]
    public void PaletteAndMenu_HaveTheCommand()
    {
        Assert.Contains("\"Trancar janelas privadas\", \"Esconde as abas privadas até desbloquear com o Windows Hello (não é uma cifra)\", \"Ctrl+Alt+L\", LockPrivateWindowsNow",
            Source("MainWindow.Palette.cs"));
        Assert.Contains("if (PrivateWindows().Count > 0) Row(\"\\uE72E\", \"Trancar janelas privadas\", LockPrivateWindowsNow, \"Ctrl+Alt+L\");", Source("MainWindow.BrowserMenu.cs"));
        Assert.Equal("Trancar janelas privadas", Eteris.Browser.Tabs.PrivateLockText.CommandTitle);
        Assert.Contains(Eteris.Browser.Palette.ShortcutSheet.Groups.Single(g => g.Title == "Janelas e página").Items,
            s => s.Keys == "Ctrl+Alt+L" && s.Action == Eteris.Browser.Tabs.PrivateLockText.ShortcutAction);
    }

    [Fact]
    public void Pages_StayHidden()
    {
        Assert.Matches(@"if \(_pause is not null\) return false;\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\) return false;", Source("MainWindow.Split.cs"));
        var webPanels = Source("MainWindow.WebPanels.cs");
        Assert.Matches(@"if \(_privateLock is not null\)\s*\{\s*HideWebPanel\(view\);\s*continue;\s*\}\s*(//[^\n]*\n\s*)*if \(_pause is not null\)", webPanels);
        Assert.Contains("SetPageVisible(browser, _sidebarPanel?.Url == url && _sidebarOpen && _pause is null && _privateLock is null);", webPanels);
        Assert.Matches(@"private bool OverlayNeeded\(\)\s*=> _pause is null && _overlayRoot is not null[^;]*&& _privateLock is null;", Source("MainWindow.Overlay.cs"));
        Assert.Contains("if (page.IsDisposed || page.Visibility != Visibility.Visible) return;", Source("MainWindow.Unseen.cs"));
    }

    // Pelo caminho de esconder da Pausa: a página que segurava uma troca e os sites fixados à parte, o resto pela disposição
    // (ShouldShow: minimizar a janela do Chromium e só depois esconder no WPF). Antes do cartão, as ferramentas de
    // programador (uma janela de topo com o DOM) e o Picture-in-Picture saem.
    [Fact]
    public void TheLock_UsesThePagePathInTheRightOrder()
    {
        var lockBody = Body(Source("MainWindow.PrivateLock.cs"), "private void LockPrivateWindow(PrivateLockReason reason)");
        Assert.Matches(@"^\s*(//[^\n]*\n\s*)*if \(!IsPrivate \|\| !IsLoaded \|\| _privateLock is not null \|\| _privateLockClosing\) return;", lockBody);
        var card = lockBody.IndexOf("ShowPrivateLockView(", StringComparison.Ordinal);
        Assert.True(card > 0);
        Assert.True(lockBody.IndexOf("HideWebPanels();", StringComparison.Ordinal) is var panels && panels > 0 && panels < card);
        Assert.True(lockBody.IndexOf("LayoutBrowsers();", StringComparison.Ordinal) is var layout && layout > 0 && layout < card);
        Assert.Matches(@"foreach \(var held in _leavingPages\.Keys\.ToList\(\)\)", lockBody);
        Assert.Contains("CloseDevTools()", lockBody);
        Assert.Contains("LeaveAllPictureInPictureScript", lockBody);
        foreach (var close in new[] { "ClosePalette();", "CloseBrowserMenu();", "CloseFind();", "CloseVaultList();", "HideLinkBubble();", "DownloadsPopup.IsOpen = false;", "CloseTransientPopups();" })
            Assert.Contains(close, lockBody);
        Assert.Contains("EnableCoveredChrome(false);", lockBody);
        Assert.Contains("UpdateOverlayVisibility();", lockBody);
        Assert.Contains("IconicThumbnail.Neutralize(", lockBody);
    }

    [Fact]
    public void TheTitle_IsNeutralAndComesBack()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        var lockBody = Body(code, "private void LockPrivateWindow(PrivateLockReason reason)");
        Assert.Contains("BindingOperations.ClearBinding(this, TitleProperty)", lockBody);
        Assert.Contains("PrivateLockText.WindowTitle", lockBody);
        Assert.Contains("BindingOperations.SetBinding(this, TitleProperty, state.TitleBinding)", Body(code, "private void UnlockPrivateWindow()"));
        Assert.DoesNotContain("SetCurrentValue(TitleProperty", code);
    }

    // As janelas auxiliares (a camada por cima da página, a bolha dos links) copiavam o título da página do momento em que
    // nasciam, legível por GetWindowText de qualquer programa mesmo com a janela escondida.
    [Fact]
    public void HelperWindows_HaveAFixedTitle()
    {
        foreach (var file in new[] { "MainWindow.Overlay.cs", "MainWindow.LinkBubble.cs" })
        {
            var code = Source(file);
            Assert.DoesNotContain("Title = Title,", code);
            Assert.Contains("Title = \"Éteris\",", code);
        }
    }

    // A árvore de automação da janela trancada só tem o cartão: as abas (o nome de cada uma traz o título), a barra de
    // endereço e a vista calma ficam de fora (um controlo desligado continua na árvore, com o nome).
    [Fact]
    public void Automation_ShowsOnlyTheCard()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new PrivateLockWindowPeer(this);", code);
        var children = Regex.Match(code, @"GetChildrenCore\(\)(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("_privateLock", children);
        Assert.Contains("CreatePeerForElement", children);
        Assert.Contains("ResetChildrenCache()", code);
        Assert.Contains("AutomationEvents.StructureChanged", code);
    }

    [Fact]
    public void LockedWindows_AreLeftOutOfThePalette()
        => Assert.Contains("w.IsPrivate == IsPrivate && w._privateLock is null", Source("MainWindow.Palette.cs"));

    // Fechar deita tudo fora, que é o que protege: sem a pergunta das abas trancadas (que ia para a fila dos avisos de uma
    // janela trancada e nunca se via). A condição vai à frente da que SessionWindowsTests fixa.
    [Fact]
    public void ALockedWindow_ClosesWithoutTheLockedTabsQuestion()
        => Assert.Contains("var locked = _privateLock is not null || Updates.UpdateService.RestartRequested || _endingPrivateSession || s_sessionFrozen ? 0 : LockedTabsAtRisk();",
            Source("MainWindow.ClosedWindows.cs"));

    // Os avisos, o painel das transferências (um Popup: janela própria, por cima de tudo) e o diálogo do certificado de
    // cliente esperam pelo desbloqueio.
    [Fact]
    public void ToastsDownloadsAndCertificates_WaitForTheUnlock()
    {
        Assert.Matches(@"if \(_pause is not null\)\s*\{\s*QueuePausedToast\(message, actions\);\s*return;\s*\}\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\)\s*\{\s*QueueLockedToast\(message, actions\);\s*return;\s*\}", Source("MainWindow.Toast.cs"));
        Assert.Contains("if (_privateLock is { } locked) locked.DownloadStarted = true;", Source("MainWindow.Downloads.cs"));
        Assert.Contains("() => _pause is null && Tabs.Tabs.Contains(tab) && IsTabOnScreen(tab) && _privateLock is null", Source("MainWindow.xaml.cs"));
        Assert.Contains("() => _pause is null && _sidebarOpen && _sidebarPanel?.Url == url && _privateLock is null", Source("MainWindow.WebPanels.cs"));
        var unlock = Body(Source("MainWindow.PrivateLock.cs"), "private void UnlockPrivateWindow()");
        Assert.Contains("ShowNextClientCertificate();", unlock);
        Assert.Contains("ShowQueuedToasts(state.Toasts);", unlock);
        Assert.Contains("if (state.DownloadStarted)", unlock);
    }

    // O diálogo do certificado de cliente já aberto (uma janela própria, sempre por cima do cartão, com o site, o título da
    // aba e o titular, e que podia enviar o certificado sem o Windows Hello) fecha ao trancar, sem resposta: o pedido volta
    // para a fila (uma recusa ficava no motor até reiniciar) e reaparece ao destrancar.
    [Fact]
    public void AnOpenCertificateDialog_IsPostponedByTheLock()
    {
        Assert.Contains("PostponeClientCertificate();", Body(Source("MainWindow.PrivateLock.cs"), "private void LockPrivateWindow(PrivateLockReason reason)"));
        var postpone = Body(Source("MainWindow.xaml.cs"), "private void PostponeClientCertificate()");
        Assert.Matches(@"if \(!_clientCertificates\.Postpone\(\)\) return;\s*foreach \(var dialog in OwnedWindows\.OfType<ClientCertificateDialog>\(\)\.ToList\(\)\) dialog\.Close\(\);", postpone);
    }

    [Fact]
    public void JsDialogs_AreSuppressedOnlyWhileLocked()
    {
        var handler = Source(Path.Combine("Browser", "PrivateLockDialogHandler.cs"));
        Assert.Matches(@"if \(_locked\(\)\)\s*\{\s*suppressMessage = true;\s*return false;\s*\}", handler);
        Assert.Contains("callback.Continue(true)", handler);
        Assert.Contains("JsDialogHandler = IsPrivate ? new PrivateLockDialogHandler(() => _lockedForPage) : null,", Source("MainWindow.xaml.cs"));
        Assert.Contains("JsDialogHandler = IsPrivate ? new PrivateLockDialogHandler(() => _lockedForPage) : null,", Source("MainWindow.WebPanels.cs"));
    }

    // Nunca a porta do cofre (a graça de 5 min, o «true» sem proteção) nem a via de teste dele (ETERIS_TEST_REAUTH=aceitar).
    [Fact]
    public void TheVerifier_IsNeverTheVaultsTestPath()
    {
        foreach (var file in new[] { "MainWindow.PrivateLock.cs", Path.Combine("Vault", "PrivateUnlock.cs") })
        {
            var code = Source(file);
            Assert.NotEmpty(code);
            foreach (var word in new[] { "ReauthGate", "Reauth.Create", "TestSwitches.Reauth", "ReauthForProfile", "TestReauth", "TestReauthMode" })
                Assert.DoesNotContain(word, code);
        }
        Assert.Contains("new WindowsReauth()", Source(Path.Combine("Vault", "PrivateUnlock.cs")));
        Assert.Contains("new PrivateUnlock(PrivateUnlock.CreateVerifier(TestSwitches.PrivateUnlockTest))", Source("MainWindow.PrivateLock.cs"));
    }

    // A janela privada continua a não escrever nada no disco.
    [Fact]
    public void NothingIsWritten()
    {
        foreach (var file in new[] { "MainWindow.PrivateLock.cs", Path.Combine("Vault", "PrivateUnlock.cs"), Path.Combine("Infrastructure", "IconicThumbnail.cs") })
        {
            var code = Source(file);
            Assert.NotEmpty(code);
            foreach (var word in new[] { "Settings.Set(", "AppData.", "SessionStore", "File.", "Directory.", "AtomicFile" })
                Assert.DoesNotContain(word, code);
        }
    }

    // Uma subscrição por processo (como a do cofre), criada depois da 1.ª página; o evento chega no fio dos SystemEvents. A
    // suspensão é síncrona (o Windows pode adormecer antes de um InvokeAsync correr), com prazo.
    [Fact]
    public void SystemEvents_AreSubscribedOnceAndDispatched()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        Assert.Single(Regex.Matches(code, Regex.Escape("SystemEvents.SessionSwitch +=")));
        Assert.Single(Regex.Matches(code, Regex.Escape("SystemEvents.PowerModeChanged +=")));
        Assert.Contains("FirstPaint.App.Then(EnsurePrivateLockHooks)", code);
        Assert.Contains("PrivateLockPolicy.LocksOn(reason)", Body(code, "private static void OnPrivateLockSessionSwitch("));
        var power = Body(code, "private static void OnPrivateLockPowerMode(");
        Assert.Contains("PrivateLockPolicy.LocksOn(mode)", power);
        Assert.Contains("Dispatcher.Invoke(", power);
        Assert.Contains("TimeSpan.FromSeconds(2)", power);
    }

    [Fact]
    public void ThePauseAndTheLock_GoTogether()
    {
        var pause = Source("MainWindow.Pause.cs");
        Assert.Matches(@"private void FocusPauseView\(\)\s*\{\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\)\s*\{\s*FocusPrivateLockView\(\);\s*return;\s*\}", pause);
        Assert.Contains("if (_privateLock is { } locked)", Body(pause, "private void ShowPausedToasts(PauseState state, int replay)"));
        var exit = Body(pause, "private void ExitPause()");
        Assert.Matches(@"EnableCoveredChrome\(true\);\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\) EnableCoveredChrome\(false\);", exit);
        // Uma transferência da Pausa numa janela trancada espera pelo desbloqueio, e não abre o painel por cima do cartão.
        Assert.Matches(@"if \(_privateLock is \{ \} lockedNow && state\.DownloadStarted\)[\s\S]*?if \(state\.DownloadStarted\) DownloadsPopup\.IsOpen = true;", exit);
        Assert.Matches(@"private bool FocusCanReturnTo\(PauseState state, UIElement element\)\s*=>[^;]*&& _privateLock is null;", pause);
        Assert.Matches(@"if \(_pause is not null\)\s*\{\s*FocusPauseView\(\);\s*return;\s*\}\s*(//[^\n]*\n\s*)*if \(_privateLock is not null\)\s*\{\s*FocusPrivateLockView\(\);\s*return;\s*\}", Source("MainWindow.Overlay.cs"));
        // A vista calma de uma janela trancada fica escondida (o aviso de captura dela nomeia o site).
        Assert.Contains("if (_privateLock is not null) view.Visibility = Visibility.Hidden;", Body(pause, "private void ShowPauseView(PauseState state)"));
    }

    // Reduzir movimento por construção: o cartão aparece de uma vez, sem animação.
    [Fact]
    public void NoMotion()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        Assert.DoesNotContain("BeginAnimation", code);
        Assert.DoesNotContain("Storyboard", code);
    }

    // Uma animação eterna só corre enquanto se vê: a vista calma escondida pela tranca não respira.
    [Fact]
    public void TheHiddenCalmView_StopsBreathing()
        => Assert.Contains("var run = state.Breathes && WindowState != WindowState.Minimized && _privateLock is null;", Source("MainWindow.Pause.cs"));

    // A marca que a prova ao vivo lê antes de clicar só existe com o verificador de teste, numa compilação com os ganchos.
    [Fact]
    public void TheTestMarker_OnlyWithTheTestVerifier()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        var marker = code.IndexOf("SetItemStatus(unlock, PrivateLockText.TestMarker(", StringComparison.Ordinal);
        Assert.True(marker > 0);
        Assert.True(code.LastIndexOf("#if ETERIS_TEST_HOOKS", marker, StringComparison.Ordinal) > code.LastIndexOf("#endif", marker, StringComparison.Ordinal));
        Assert.Matches(@"if \([^\n]*is TestPrivateUnlockVerifier[^\n]*\)\s*AutomationProperties\.SetItemStatus\(unlock, PrivateLockText\.TestMarker\(", code);
    }

    // O bloqueio do Windows com o diálogo do Hello aberto: a pergunta é cancelada antes de qualquer janela (a que mostra o
    // diálogo já está trancada e sai logo no início do LockPrivateWindow).
    [Fact]
    public void LockCancelsAPendingQuestion_BeforeTheWindowLoop()
    {
        var body = Body(Source("MainWindow.PrivateLock.cs"), "private static void LockPrivateWindows(PrivateLockReason reason)");
        var cancel = body.IndexOf("s_unlocking?.Cancel();", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && cancel < body.IndexOf("foreach", StringComparison.Ordinal));
    }

    // O verificador nunca destranca sozinho: só o resultado Unlocked/UnlockedUnprotected do PrivateUnlock chama o
    // UnlockPrivateWindow, e nenhuma outra coisa (definições, desbloqueio do Windows, janela nova) o faz.
    [Fact]
    public void OnlyTheWindowsAnswerUnlocks()
    {
        var code = Source("MainWindow.PrivateLock.cs");
        Assert.Single(Regex.Matches(code, @"\.UnlockPrivateWindow\(\)"));
        Assert.Matches(@"if \(result is PrivateUnlockResult\.Unlocked or PrivateUnlockResult\.UnlockedUnprotected\)\s*\{[^}]*\.UnlockPrivateWindow\(\)", code);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.EndsWith("MainWindow.PrivateLock.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("UnlockPrivateWindow", File.ReadAllText(file));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Eteris.Browser"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Source(string relative)
    {
        var path = Path.Combine(Root, "src", "Eteris.Browser", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
