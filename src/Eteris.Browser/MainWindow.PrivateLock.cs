using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;
using Eteris.Browser.Vault;
using Microsoft.Win32;
using MediaState = Eteris.Browser.Tabs.MediaState;

namespace Eteris.Browser;

// As janelas privadas trancadas com o Windows Hello (proposta 5): ao bloquear o Windows, ao suspender, com a janela parada
// o tempo escolhido nas Definições (nunca com som, vídeo ou uma chamada) ou com «Trancar janelas privadas» (Ctrl+Alt+L,
// paleta, menu ⋯), cada janela privada esconde as abas atrás de um cartão que só abre com o Windows Hello ou, sem ele, a
// palavra-passe do Windows. Destrancar uma destranca todas as trancadas; cancelar deixa-as trancadas. Como no Safari, uma
// janela privada só com abas novas não se tranca sozinha.
//
// Como a Pausa (MainWindow.Pause.cs), é um estado da janela (_privateLock) que os pontos onde o Éteris decide o que se vê e
// para onde vão as teclas consultam: a disposição das páginas (ShouldShow), os sites fixados, a camada por cima da página
// (OverlayNeeded), os avisos (ShowToast), as transferências, o certificado de cliente, o foco, o teclado (também o da
// página, no fio do CEF: _lockedForPage) e o rato. Por cima disso, trancada, a janela não diz nada da página: o título é
// neutro, a árvore de automação só tem o cartão (PrivateLockWindowPeer), a miniatura do Alt+Tab é uma imagem neutra
// (Infrastructure/IconicThumbnail.cs) e os diálogos das páginas não aparecem (Browser/PrivateLockDialogHandler.cs).
//
// Uma camada de conveniência, não uma cifra: as abas continuam na memória do processo e as páginas continuam a correr (a
// música e a chamada continuam). A tranca não para nada; «Pausar tudo (Ctrl+.)» no cartão entra na Pausa sem destrancar.
// Nada fica gravado: a janela privada continua a não escrever nada no disco. O que é puro está em Tabs/PrivateLockPolicy.cs,
// Tabs/PrivateLockText.cs e Vault/PrivateUnlock.cs.
public partial class MainWindow
{
    private sealed class PrivateLockState(PrivateLockReason reason)
    {
        public PrivateLockReason Reason { get; } = reason;
        public PrivateLockSurface? View;
        public TextBlock? Heading;
        public TextBlock? Line;
        public Button? Unlock;
        public TextBlock? Status;
        public FrameworkElement? Sound;
        public TextBlock? Capture;
        // A ligação do título da janela ao título da aba ativa: sai ao trancar e volta ao destrancar.
        public BindingBase? TitleBinding;
        // Os avisos pedidos com a janela trancada (ShowToast): aparecem ao destrancar.
        public List<(string Message, ToastAction[] Actions)> Toasts { get; } = [];
        // Começou uma transferência com a janela trancada: o painel das transferências abre ao destrancar.
        public bool DownloadStarted;
    }

    private PrivateLockState? _privateLock;

    // A mesma tranca lida no fio do CEF (ShortcutFromPage, os diálogos das páginas): nenhuma tecla vai para a página.
    private volatile bool _lockedForPage;

    // A janela está a fechar: não se tranca (o cartão ia aparecer a meio do fecho).
    private bool _privateLockClosing;

    // A última vez que a janela esteve em uso (relógio monotónico) e o relógio da inatividade.
    private TimeSpan _privateLastUse;
    private DispatcherTimer? _privateIdleTimer;

    // As janelas privadas abertas, lido também no fio do CEF (o Ctrl+Alt+L só é do Éteris com uma aberta).
    private static volatile int s_privateWindowsOpen;

    // A confirmação em curso (uma de cada vez, para todas as janelas) e a janela dona do diálogo do Windows.
    private static CancellationTokenSource? s_unlocking;
    private static MainWindow? s_unlockingOwner;

    // A tranca das janelas privadas pede SEMPRE o Windows a sério (Vault/PrivateUnlock.cs): a via de teste só sabe responder
    // «cancelado» ou «indisponível», e nunca é a do cofre.
    private static PrivateUnlock? s_privateUnlock;
    private static PrivateUnlock PrivateUnlocker => s_privateUnlock ??= new PrivateUnlock(PrivateUnlock.CreateVerifier(TestSwitches.PrivateUnlockTest));

    // Com Windows Hello ou com a palavra-passe do Windows (lido fora do fio da interface e guardado): até se saber, a do último
    // uso; na 1.ª vez, «com o Windows Hello».
    private static ReauthAvailability s_privateLockAvailability = ReauthAvailability.Hello;
    private static bool s_privateLockAvailabilityPending;

    private static bool s_privateLockHooks;
    private static DispatcherTimer? s_privateLockTestTimer;
    private static bool s_privateLockTestFired;

    private static TimeSpan PrivateLockClock() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // O cartão. Um Grid não tem par de automação: sem este, o nome «Janela privada trancada» nunca chegava ao leitor de ecrã.
    internal sealed class PrivateLockSurface : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PrivateLockSurfacePeer(this);

        private sealed class PrivateLockSurfacePeer(PrivateLockSurface owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

            protected override string GetClassNameCore() => nameof(PrivateLockSurface);
        }
    }

    // O cadeado do cartão é decorativo: sem par de automação (um carácter da fonte de ícones lido em voz alta não diz nada).
    private sealed class LockGlyph : TextBlock
    {
        protected override AutomationPeer OnCreateAutomationPeer() => null!;
    }

    // A janela, para o leitor de ecrã e a UI Automation: trancada, só o cartão. As abas (o nome de cada uma traz o título), a
    // barra de endereço, as abas verticais e a vista calma ficam de fora; desligar não bastava (um controlo desligado
    // continua na árvore, com o nome).
    protected override AutomationPeer OnCreateAutomationPeer() => new PrivateLockWindowPeer(this);

    private sealed class PrivateLockWindowPeer(MainWindow owner) : WindowAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            if (owner._privateLock is not { } locked) return base.GetChildrenCore();
            return locked.View is { } view && UIElementAutomationPeer.CreatePeerForElement(view) is { } card ? [card] : [];
        }

        protected override string GetNameCore() => owner.Title ?? string.Empty;
    }

    private void RefreshPrivateLockAutomation()
    {
        if (UIElementAutomationPeer.FromElement(this) is not PrivateLockWindowPeer peer) return;
        peer.ResetChildrenCache();
        peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }

    // ---------- A janela privada ----------

    private void WatchPrivateLock()
    {
        if (!IsPrivate) return;
        var counted = false;
        _privateLastUse = PrivateLockClock();
        Loaded += (_, _) =>
        {
            s_privateWindowsOpen++;
            counted = true;
            _privateLastUse = PrivateLockClock();
            StartPrivateIdleTimer();
            // A subscrição dos SystemEvents regista a sessão no Windows: depois da 1.ª página, fora do arranque (um arranque
            // com --private faz da 1.ª janela uma privada).
            FirstPaint.App.Then(EnsurePrivateLockHooks);
        };
        ContentRendered += (_, _) => ArmPrivateLockTest();
        void OnSettings(UserSettings _) => Dispatcher.InvokeAsync(StartPrivateIdleTimer);
        Settings.Changed += OnSettings;
        Closing += (_, e) =>
        {
            if (!e.Cancel) _privateLockClosing = true;
        };
        Closed += (_, _) =>
        {
            Settings.Changed -= OnSettings;
            if (counted) s_privateWindowsOpen--;
            _privateIdleTimer?.Stop();
            // A janela dona do diálogo do Windows fechou: a pergunta é cancelada e as outras continuam trancadas.
            if (ReferenceEquals(s_unlockingOwner, this)) s_unlocking?.Cancel();
        };
        // Ativada trancada (o regresso da Pausa numa janela inativa pode ter deixado o foco lógico na página escondida).
        Activated += (_, _) =>
        {
            if (_privateLock is null) return;
            FocusPrivateLockView();
            Dispatcher.InvokeAsync(FocusPrivateLockView, DispatcherPriority.Input);
        };
        // Sair da janela é ela própria uma entrada: a inatividade conta a partir daqui.
        Deactivated += (_, _) =>
        {
            if (_privateLock is null) _privateLastUse = PrivateLockPolicy.LastUse(_privateLastUse, active: true, PrivateLockClock(), SystemInputAgo());
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(PrivateLockWindowHook);
    }

    // A miniatura (barra de tarefas, Alt+Tab, vista de tarefas): trancada, a imagem neutra.
    private nint PrivateLockWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_privateLock is not null && IconicThumbnail.Answer(hwnd, message, lParam)) handled = true;
        return 0;
    }

    private bool HasPrivateContent() => Tabs.Workspaces.SelectMany(Tabs.TabsOf).Any(t => !NewTabPage.IsNewTab(t.Address));

    private static List<MainWindow> LockedPrivateWindows() => PrivateWindows().Where(w => w._privateLock is not null).ToList();

    // ---------- Os gatilhos ----------

    // Uma subscrição por processo (como a do cofre), que nunca se desfaz. Os eventos chegam no fio dos SystemEvents: nada do
    // WPF se lê lá.
    private static void EnsurePrivateLockHooks()
    {
        if (s_privateLockHooks) return;
        s_privateLockHooks = true;
        SystemEvents.SessionSwitch += (_, e) => OnPrivateLockSessionSwitch(e.Reason);
        SystemEvents.PowerModeChanged += (_, e) => OnPrivateLockPowerMode(e.Mode);
    }

    // O Windows bloqueou (Win+L), a consola ou a ligação remota desligou, ou terminou a sessão. Também o gancho de teste
    // ETERIS_TEST_TRANCA_BLOQUEIO (que nunca bloqueia o Windows).
    private static void OnPrivateLockSessionSwitch(SessionSwitchReason reason)
    {
        if (!PrivateLockPolicy.LocksOn(reason)) return;
        Application.Current?.Dispatcher.InvokeAsync(() => LockPrivateWindows(PrivateLockReason.WindowsLocked));
    }

    // A suspensão: síncrona, com prazo (o Windows pode adormecer antes de um InvokeAsync correr); ao acordar, de recurso.
    private static void OnPrivateLockPowerMode(PowerModes mode)
    {
        if (Application.Current is not { } app) return;
        if (PrivateLockPolicy.LocksOn(mode))
        {
            try { app.Dispatcher.Invoke(() => LockPrivateWindows(PrivateLockReason.Suspended), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
        else if (mode == PowerModes.Resume) app.Dispatcher.InvokeAsync(() => LockPrivateWindows(PrivateLockReason.Suspended));
    }

    // ETERIS_TEST_TRANCA_BLOQUEIO=<s>: uma só vez por processo, <s> segundos depois de a janela privada aberta mais recentemente
    // se mostrar, simula «o Windows bloqueou» pelo mesmo caminho dos SystemEvents. Só tranca.
    private void ArmPrivateLockTest()
    {
        if (s_privateLockTestFired || TestSwitches.LockAfterSeconds is not { } seconds) return;
        s_privateLockTestTimer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (s_privateLockTestFired) return;
            s_privateLockTestFired = true;
            OnPrivateLockSessionSwitch(SessionSwitchReason.SessionLock);
        };
        s_privateLockTestTimer = timer;
        timer.Start();
    }

    // «Trancar janelas privadas» (Ctrl+Alt+L, paleta, menu ⋯), de qualquer janela: todas as privadas abertas, também as que
    // só têm abas novas. Numa janela normal, um aviso diz quantas; numa privada, o cartão diz tudo.
    private void LockPrivateWindowsNow()
    {
        if (PrivateWindows().Count == 0) return;
        LockPrivateWindows(PrivateLockReason.Manual);
        var locked = LockedPrivateWindows().Count;
        if (!IsPrivate && locked > 0) ShowToast(PrivateLockText.LockedToast(locked, s_privateLockAvailability));
    }

    // Os gatilhos automáticos e o comando trancam todas as janelas privadas abertas, cada uma pela sua regra (as automáticas
    // só as que têm uma página).
    private static void LockPrivateWindows(PrivateLockReason reason)
    {
        // Antes de tudo, a confirmação em curso é cancelada: a janela que mostra o diálogo já está trancada e sai logo no
        // LockPrivateWindow, e é exatamente o caso do bloqueio do Windows com o diálogo do Hello aberto.
        s_unlocking?.Cancel();
        var settings = Settings.Current;
        foreach (var window in PrivateWindows())
            if (PrivateLockPolicy.ShouldLock(reason, settings.PrivateLock, window.HasPrivateContent(), window._privateLock is not null))
                window.LockPrivateWindow(reason);
    }

    // A inatividade é por janela (como no Safari, conta a janela e não a aplicação). O relógio só corre com a definição
    // ligada, um tempo escolhido e a janela destrancada; lê as definições em cada tique (mudá-las nunca destranca).
    private void StartPrivateIdleTimer()
    {
        var settings = Settings.Current;
        if (!IsPrivate || !IsLoaded || _privateLock is not null || _privateLockClosing || !settings.PrivateLock || settings.PrivateLockMinutes <= 0)
        {
            _privateIdleTimer?.Stop();
            return;
        }
        if (_privateIdleTimer is null)
        {
            _privateIdleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _privateIdleTimer.Tick += (_, _) => TickPrivateIdle();
        }
        _privateIdleTimer.Interval = TestSwitches.IdleSeconds is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1);
        if (!_privateIdleTimer.IsEnabled) _privateIdleTimer.Start();
    }

    private void TickPrivateIdle()
    {
        var settings = Settings.Current;
        if (_privateLock is not null || !IsLoaded || !settings.PrivateLock)
        {
            _privateIdleTimer?.Stop();
            return;
        }
        var now = PrivateLockClock();
        _privateLastUse = PrivateLockPolicy.LastUse(_privateLastUse, PrivateLockWindowInUse(), now, SystemInputAgo());
        var limit = PrivateLockPolicy.IdleLimit(settings.PrivateLockMinutes, TestSwitches.IdleSeconds);
        if (!PrivateLockPolicy.IdleLockDue(limit, now - _privateLastUse, PrivateLockPlaying(), PrivateLockCapturing())) return;
        if (PrivateLockPolicy.ShouldLock(PrivateLockReason.Idle, settings.PrivateLock, HasPrivateContent(), alreadyLocked: false))
            LockPrivateWindow(PrivateLockReason.Idle);
    }

    // Em uso: a janela ativa, a camada por cima dela (a barra de procura, o cartão de permissão) ou uma janela dela.
    private bool PrivateLockWindowInUse()
        => IsActive || _overlay?.IsActive == true || OwnedWindows.Cast<Window>().Any(w => w.IsActive);

    // Som ou vídeo a tocar em algum motor da janela (durante a Pausa nada toca: a Pausa pausou-o).
    private bool PrivateLockPlaying()
        => _browsers.Keys.Concat(_videoViews.Keys).Any(t => t.Media == MediaState.Playing || t.IsAudible);

    // Câmara, microfone ou ecrã em uso por uma aba ou um site fixado.
    private bool PrivateLockCapturing() => _browsers.Values.Concat(_webPanelBrowsers.Values).Any(IsCapturing);

    // A última entrada do sistema (teclado, rato, toque): GetLastInputInfo, com a volta dos 49,7 dias.
    private static TimeSpan SystemInputAgo()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? PrivateLockPolicy.InputAgo(unchecked((uint)Environment.TickCount), info.Time) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    // ---------- Trancar ----------

    private void LockPrivateWindow(PrivateLockReason reason)
    {
        if (!IsPrivate || !IsLoaded || _privateLock is not null || _privateLockClosing) return;
        var state = new PrivateLockState(reason);
        _privateLock = state;
        // Daqui em diante nenhuma tecla vai para a página.
        _lockedForPage = true;

        // 1. O que estava aberto por cima da página fecha (o tempo e o bloqueio chegam sem entrada do utilizador: nada os
        // fechou). O pedido de permissão pendente fica pendente na camada escondida e reaparece ao destrancar.
        ClosePalette();
        CloseBrowserMenu();
        CloseGlance();
        CloseRadar();
        CloseSuggestions();
        CloseShortcuts();
        CloseObservatory();
        CloseQr();
        CloseRename();
        CloseVaultList();
        HideGroupPreview();
        MediaPopup.IsOpen = false;
        DownloadsPopup.IsOpen = false;
        ShieldPopup.IsOpen = false;
        SiteInfoPopup.IsOpen = false;
        TabListPopup.IsOpen = false;
        LinkStatusBubble.Visibility = Visibility.Collapsed;
        HideLinkBubble();
        // A barra de procura guarda o texto procurado; os menus de contexto e as dicas (o cartão da aba traz o título).
        CloseFind();
        CloseTransientPopups();

        // 2. As ferramentas de programador (uma janela de topo com o DOM da página) e o Picture-in-Picture (a janela flutuante
        // do Chromium, sempre por cima): o vídeo volta para a página escondida e continua a tocar.
        foreach (var browser in _browsers.Values.Concat(_webPanelBrowsers.Values).ToList())
        {
            if (!IsReady(browser)) continue;
            try { browser.GetBrowserHost()?.CloseDevTools(); }
            catch (Exception) { }
            PageScript.Run(browser, MediaInfo.LeaveAllPictureInPictureScript);
        }
        foreach (var view in _videoViews.Values.ToList())
            if (view.CoreWebView2 is { } core) _ = VideoPageScript.EvaluateNumberAsync(core, MediaInfo.LeaveAllPictureInPictureScript);

        // 3. Nenhuma página à vista, pelo caminho da Pausa: a que segurava uma troca e os sites fixados à parte, o resto pela
        // disposição (ShouldShow devolve false: minimizar a janela do Chromium e só depois esconder no WPF).
        foreach (var held in _leavingPages.Keys.ToList())
        {
            _leavingPages.Remove(held);
            HidePage(held);
        }
        HideWebPanels();
        LayoutBrowsers();

        // 4. O cartão, por cima de tudo (também da vista calma da Pausa, que fica escondida: o aviso de captura dela nomeia o
        // site).
        ShowPrivateLockView(state);
        EnableCoveredChrome(false);
        UpdateOverlayVisibility();
        if (_pause is { } paused)
        {
            if (paused.View is { } calm) calm.Visibility = Visibility.Hidden;
            BreatheWhileSeen(paused);
        }

        // 5. O título neutro (a barra de tarefas, o Alt+Tab e o GetWindowText de qualquer programa).
        state.TitleBinding = BindingOperations.GetBindingBase(this, TitleProperty);
        BindingOperations.ClearBinding(this, TitleProperty);
        Title = PrivateLockText.WindowTitle;

        // 6. A árvore de automação passa a ter só o cartão, e a miniatura passa a imagem neutra.
        RefreshPrivateLockAutomation();
        IconicThumbnail.Neutralize(new WindowInteropHelper(this).Handle, on: true);

        // 7. O foco no botão (o foco do Win32 podia ter ficado na página escondida) e o anúncio, só na janela ativa.
        FocusPrivateLockView();
        Dispatcher.InvokeAsync(FocusPrivateLockView, DispatcherPriority.Input);
        if (IsActive && state.Heading is { } heading) Sync.LiveRegion.Announce(heading);

        // 8. O relógio da inatividade para.
        _privateIdleTimer?.Stop();
    }

    // Os menus de contexto (da aba, da página, da barra de endereço, da seleção) e as dicas abertas desta janela: cada um é
    // uma janela própria por cima de tudo, e uma dica do cartão da aba traz o título.
    private void CloseTransientPopups()
    {
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToList())
        {
            if (source.IsDisposed || source.RootVisual is not Visual root) continue;
            var pending = new Queue<(DependencyObject Node, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0)
            {
                var (node, depth) = pending.Dequeue();
                if (node is ContextMenu { IsOpen: true } menu)
                {
                    if (BelongsHere(menu.PlacementTarget)) menu.IsOpen = false;
                    continue;
                }
                if (node is ToolTip { IsOpen: true } tip)
                {
                    if (BelongsHere(tip.PlacementTarget)) tip.IsOpen = false;
                    continue;
                }
                if (depth >= 8 || node is not Visual) continue;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Enqueue((VisualTreeHelper.GetChild(node, i), depth + 1));
            }
        }

        bool BelongsHere(UIElement? target) => target is not null && ReferenceEquals(Window.GetWindow(target), this);
    }

    // ---------- O cartão ----------

    private void ShowPrivateLockView(PrivateLockState state)
    {
        var view = BuildPrivateLockView(state);
        state.View = view;
        Grid.SetRow(view, 0);
        Grid.SetRowSpan(view, 3);
        Grid.SetColumnSpan(view, 1);
        // Acima da vista calma da Pausa (1000).
        Panel.SetZIndex(view, 1100);
        ((Grid)RootBorder.Child).Children.Add(view);
        RefreshPrivateLockActivity();
        RefreshPrivateLockAvailability();
    }

    private void FocusPrivateLockView()
    {
        if (_privateLock?.Unlock is not { } unlock) return;
        if (_overlay is { IsActive: true }) Activate();
        // Só a janela ativa recebe o teclado; as outras guardam o botão como foco lógico (um Keyboard.Focus numa janela
        // inativa podia ativá-la).
        if (IsActive) Keyboard.Focus(unlock);
        else FocusManager.SetFocusedElement(this, unlock);
    }

    // Só o que é do cartão: nada da página (nem título, nem endereço, nem o nome de um site) é lido aqui.
    private PrivateLockSurface BuildPrivateLockView(PrivateLockState state)
    {
        var highContrast = SystemParameters.HighContrast;
        var mini = _miniMode;

        void Ink(TextBlock text)
        {
            if (highContrast) text.Foreground = SystemColors.WindowTextBrush;
            else text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }

        TextBlock Text(string content, double size, Thickness margin)
        {
            var text = new TextBlock
            {
                Text = content,
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = margin,
            };
            Ink(text);
            return text;
        }

        var view = new PrivateLockSurface { ClipToBounds = true, Focusable = false };
        AutomationProperties.SetName(view, PrivateLockText.CardTitle);
        KeyboardNavigation.SetTabNavigation(view, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(view, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(view, KeyboardNavigationMode.Cycle);
        // Opaco nos dois temas (sem paisagem nem imagem da página); em alto contraste, as cores do sistema.
        if (highContrast) view.Background = SystemColors.WindowBrush;
        else view.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");

        view.Children.Add(BuildCaptionCard(mini, highContrast));

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Margin = new Thickness(16),
        };

        // O cadeado, estático. O texto do tema (e não a «Cor do Éteris», escolhida pelo utilizador: o contraste não se podia
        // garantir); em alto contraste, a cor de realce do sistema.
        var glyph = new LockGlyph
        {
            Text = "",
            FontSize = mini ? 24 : 48,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        if (highContrast) glyph.Foreground = SystemColors.HighlightBrush;
        else glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        center.Children.Add(glyph);

        var title = Text(PrivateLockText.CardTitle, mini ? 16 : 22, new Thickness(0, mini ? 6 : 14, 0, 0));
        title.FontWeight = FontWeights.SemiBold;
        center.Children.Add(title);

        var line = Text(PrivateLockText.CardLine(s_privateLockAvailability), mini ? 12.5 : 14, new Thickness(0, mini ? 4 : 8, 0, 0));
        center.Children.Add(line);

        var unlockText = PrivateLockText.UnlockButton(s_privateLockAvailability);
        var unlock = new Button
        {
            Content = unlockText,
            Style = FocusableButtonStyle("PrimaryPillButton", highContrast ? SystemColors.HighlightBrush : null),
            Padding = mini ? new Thickness(14, 5, 14, 5) : new Thickness(24, 8, 24, 8),
            FontSize = mini ? 12.5 : 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, mini ? 10 : 20, 0, 0),
        };
        AutomationProperties.SetName(unlock, unlockText);
        AutomationProperties.SetAcceleratorKey(unlock, "Enter");
#if ETERIS_TEST_HOOKS
        // A marca que a prova ao vivo lê antes de clicar, só com o verificador de teste (nunca abre o diálogo do Windows).
        if (PrivateUnlocker.Verifier is TestPrivateUnlockVerifier test) AutomationProperties.SetItemStatus(unlock, PrivateLockText.TestMarker(test.Mode));
#endif
        unlock.Click += (_, _) => UnlockPrivateWindowsAsync();
        center.Children.Add(unlock);

        // O estado da confirmação: região viva educada, vazia no início.
        var status = Text(string.Empty, mini ? 12 : 13.5, new Thickness(0, 10, 0, 0));
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        center.Children.Add(status);

        // A janela continua a tocar (a tranca não para nada): «Pausar tudo (Ctrl+.)» entra na Pausa sem destrancar.
        var sound = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        sound.Children.Add(Text(PrivateLockText.StillPlaying, mini ? 12 : 13.5, new Thickness(0, mini ? 8 : 16, 0, 0)));
        var pauseAll = new Button
        {
            Content = PrivateLockText.PauseAll,
            Style = FocusableButtonStyle("PillButton", highContrast ? SystemColors.HighlightBrush : null),
            Padding = mini ? new Thickness(10, 3, 10, 3) : new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AutomationProperties.SetName(pauseAll, PrivateLockText.PauseAll);
        AutomationProperties.SetAcceleratorKey(pauseAll, "Ctrl+.");
        pauseAll.Click += (_, _) => TogglePause();
        sound.Children.Add(pauseAll);
        center.Children.Add(sound);

        // Uma chamada pode continuar: diz-se, sem o nome do site.
        var capture = Text(PrivateLockText.Capturing, mini ? 12 : 13.5, new Thickness(0, mini ? 8 : 12, 0, 0));
        capture.FontWeight = FontWeights.SemiBold;
        capture.Visibility = Visibility.Collapsed;
        center.Children.Add(capture);

        // A nota honesta (fora da janela flutuante): o texto secundário do tema tem pelo menos 4,5:1 sobre a superfície nos
        // dois temas (PrivateLockAccessibilityTests).
        if (!mini)
        {
            var honest = Text(PrivateLockText.Honest, 12, new Thickness(0, 24, 0, 0));
            if (!highContrast) honest.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            center.Children.Add(honest);
        }

        view.Children.Add(center);

        // Na janela flutuante não há barra de título: o cartão arrasta-se.
        view.MouseLeftButtonDown += (_, e) =>
        {
            if (_miniMode && e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) DragMove();
        };

        state.Heading = title;
        state.Line = line;
        state.Unlock = unlock;
        state.Status = status;
        state.Sound = sound;
        state.Capture = capture;
        return view;
    }

    // O som e a captura no cartão (também no RefreshMediaHub desta janela): «Pausar tudo» só com som e sem Pausa.
    private void RefreshPrivateLockActivity()
    {
        if (_privateLock is not { } state) return;
        if (state.Sound is { } sound) sound.Visibility = _pause is null && PrivateLockPlaying() ? Visibility.Visible : Visibility.Collapsed;
        if (state.Capture is { } capture) capture.Visibility = PrivateLockCapturing() ? Visibility.Visible : Visibility.Collapsed;
    }

    // Com Windows Hello ou com a palavra-passe do Windows: lido fora do fio da interface (COM num fio MTA, que espera).
    private static async void RefreshPrivateLockAvailability()
    {
        if (s_privateLockAvailabilityPending) return;
        s_privateLockAvailabilityPending = true;
        try
        {
            var verifier = PrivateUnlocker.Verifier;
            s_privateLockAvailability = await Task.Run(verifier.CheckAvailability);
        }
        catch (Exception) { }
        finally { s_privateLockAvailabilityPending = false; }
        foreach (var window in LockedPrivateWindows()) window.ShowPrivateLockAvailability();
    }

    private void ShowPrivateLockAvailability()
    {
        if (_privateLock is not { } state) return;
        if (state.Line is { } line) line.Text = PrivateLockText.CardLine(s_privateLockAvailability);
        if (state.Unlock is { } unlock)
        {
            var text = PrivateLockText.UnlockButton(s_privateLockAvailability);
            unlock.Content = text;
            AutomationProperties.SetName(unlock, text);
        }
    }

    private void ShowPrivateLockStatus(string? text, bool canUnlock)
    {
        if (_privateLock is not { } state) return;
        if (state.Unlock is { } unlock) unlock.IsEnabled = canUnlock;
        if (state.Status is not { } status) return;
        status.Text = text ?? string.Empty;
        if (text is not null && IsActive) Sync.LiveRegion.Announce(status);
    }

    // Os avisos pedidos com a janela trancada (ShowToast): aparecem ao destrancar.
    private void QueueLockedToast(string message, ToastAction[] actions) => _privateLock!.Toasts.Add((message, actions));

    // Os avisos em fila, juntos num só (o lugar do aviso é um), como no regresso da Pausa: as mensagens por ordem e sem
    // repetir, e as ações dos mais recentes até ao máximo de um aviso. Pelo ShowToast: com a Pausa ligada, vão para a fila dela.
    private void ShowQueuedToasts(List<(string Message, ToastAction[] Actions)> toasts)
    {
        if (toasts.Count == 0) return;
        var actions = toasts.AsEnumerable().Reverse().SelectMany(t => t.Actions).Take(ToastPolicy.MaxActions).ToArray();
        ShowToast(string.Join("\n", toasts.Select(t => t.Message).Distinct(StringComparer.Ordinal)), actions);
    }

    // ---------- O teclado ----------

    // Trancada, só o cartão recebe teclas: Tab, Enter, Espaço e as setas (o botão, «Pausar tudo», os botões da janela) e o
    // Alt+F4 seguem o caminho normal do WPF; o Ctrl+. continua a ser a Pausa; tudo o resto é engolido (também o Esc, que não
    // destranca nem fecha, o Ctrl+W e o próprio Ctrl+Alt+L).
    private bool HandlePrivateLockKey(Key key, bool ctrl, bool shift, bool alt, bool repeat)
    {
        if (IsPauseToggleKey(key, ctrl, shift, alt))
        {
            if (!repeat) TogglePause();
            return true;
        }
        if (!ctrl && !alt && key is Key.Tab or Key.Enter or Key.Space or Key.Left or Key.Right or Key.Up or Key.Down) return false;
        if (alt && !ctrl && !shift && key == Key.F4) return false;
        return true;
    }

    // ---------- Destrancar ----------

    // O botão «Desbloquear» (ou Enter/Espaço com o foco nele): pede o Windows uma vez para todas as janelas trancadas.
    private async void UnlockPrivateWindowsAsync()
    {
        if (s_unlocking is not null || _privateLock is null) return;
        var cancel = new CancellationTokenSource();
        s_unlocking = cancel;
        s_unlockingOwner = this;
        foreach (var window in LockedPrivateWindows()) window.ShowPrivateLockStatus(PrivateLockText.Confirming, canUnlock: false);
        PrivateUnlockResult result;
        try { result = await PrivateUnlocker.RequestAsync(new WindowInteropHelper(this).Handle, cancel.Token); }
        catch (Exception) { result = PrivateUnlockResult.Failed; }
        finally
        {
            s_unlocking = null;
            s_unlockingOwner = null;
            cancel.Dispose();
        }
        var locked = LockedPrivateWindows();
        if (result is PrivateUnlockResult.Unlocked or PrivateUnlockResult.UnlockedUnprotected)
        {
            var now = PrivateLockClock();
            foreach (var window in locked) window.UnlockPrivateWindow();
            foreach (var window in PrivateWindows()) window._privateLastUse = now;
            // A conta do Windows sem proteção: destranca e diz, com honestidade, que a tranca não protege nada.
            if (result == PrivateUnlockResult.UnlockedUnprotected && IsLoaded && !_privateLockClosing) ShowToast(PrivateLockText.Unprotected);
            return;
        }
        // Cancelado, falhou ou ocupado: continua tudo trancado, com o estado e o botão ligado. Interrompido (um bloqueio, um
        // fecho): continua trancado, sem texto.
        var status = PrivateLockText.Status(result);
        foreach (var window in locked) window.ShowPrivateLockStatus(status, canUnlock: true);
        locked.FirstOrDefault(w => w.IsActive)?.FocusPrivateLockView();
    }

    // Pela ordem inversa do trancar, com a Pausa em conta (a Pausa e a tranca são estados independentes da janela).
    private void UnlockPrivateWindow()
    {
        if (_privateLock is not { } state) return;
        _privateLock = null;
        _lockedForPage = false;

        // 1. O cartão sai; a vista calma, se a Pausa continua, volta a ver-se (e a respirar).
        if (state.View is { } view) ((Grid)RootBorder.Child).Children.Remove(view);
        if (_pause is { } paused)
        {
            if (paused.View is { } calm) calm.Visibility = Visibility.Visible;
            BreatheWhileSeen(paused);
        }

        // 2. O título volta a seguir a aba ativa; a miniatura e a árvore de automação voltam ao normal.
        if (state.TitleBinding is not null) BindingOperations.SetBinding(this, TitleProperty, state.TitleBinding);
        else ClearValue(TitleProperty);
        IconicThumbnail.Neutralize(new WindowInteropHelper(this).Handle, on: false);
        RefreshPrivateLockAutomation();
        EnableCoveredChrome(_pause is null);

        // 3. As páginas, pela disposição normal (com a Pausa ligada continuam escondidas: ShouldShow) e o site fixado aberto.
        LayoutBrowsers();
        if (_pause is null && _sidebarOpen && CurrentWebPanelBrowser is not null && _sidebarPanel is { } panel) ShowWebPanel(panel, focus: false);

        // 4. O que esperou: a camada, o certificado de cliente, o painel das transferências e os avisos (com a Pausa ligada,
        // para a fila dela).
        UpdateOverlayVisibility();
        ShowNextClientCertificate();
        if (state.DownloadStarted)
        {
            if (_pause is { } pausedNow) pausedNow.DownloadStarted = true;
            else DownloadsPopup.IsOpen = true;
        }
        ShowQueuedToasts(state.Toasts);

        // 5. O foco: na Pausa, «Voltar»; senão, a página.
        if (_pause is not null) FocusPauseView();
        else if (IsActive) ReturnFocusToPage();
        else if (ActiveBrowser is { } page) FocusManager.SetFocusedElement(this, page);

        // 6. O relógio da inatividade recomeça.
        _privateLastUse = PrivateLockClock();
        StartPrivateIdleTimer();
    }
}
