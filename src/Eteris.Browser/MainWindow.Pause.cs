using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;
using Eteris.Browser.Theme;
using Microsoft.Web.WebView2.Wpf;
using MediaState = Eteris.Browser.Tabs.MediaState;

namespace Eteris.Browser;

// A Pausa (Ctrl+., paleta ou menu ⋯): cala e pausa o som e o vídeo de todas as janelas abertas (normais, privadas e a
// flutuante), esconde as páginas e mostra um ecrã calmo — a paisagem da nova aba desfocada e escurecida, um anel que
// respira (4 s a inspirar, 6 s a expirar), um temporizador opcional e «Voltar». Ao voltar fica tudo como estava, mas o
// que tocava não recomeça sozinho. Opcional e discreta: o fim do temporizador nunca acaba a Pausa nem toca som.
//
// A Pausa é um estado da janela (_pause) que os pontos onde o Éteris decide o que se vê e o que se ouve consultam, e não
// uma varredura desfeita ao voltar: a disposição das páginas (ShouldShow, MainWindow.Split.cs), os sites fixados
// (MainWindow.WebPanels.cs), o som de cada motor (ToggleMute, o motor que nasce, o motor de vídeo), a camada por cima da
// página (OverlayNeeded), os avisos (ShowToast) e o foco (ReturnFocusToPage). O que é puro está em Tabs/PausePlan.cs,
// Tabs/PauseTimer.cs e Theme/Motion.cs.
//
// Não se guarda: fechar o Éteris durante a Pausa faz o arranque seguinte normal. As páginas escondidas continuam a correr
// (como as abas de fundo: o correio e o chat continuam a receber mensagens) e as transferências continuam.
public partial class MainWindow
{
    private sealed class PauseState(PausePlan<FrameworkElement> plan)
    {
        public PausePlan<FrameworkElement> Plan { get; } = plan;
        public Grid? View;
        public ScaleTransform? RingScale;
        public Ellipse? Ring;
        public TextBlock? Breath;
        public TextBlock? Countdown;
        public TextBlock? Clock;
        public Button? Back;
        public Panel? Chips;
        // A respiração foi pedida (o anel respira quando a janela se vê) e está a correr.
        public bool Breathes;
        public bool Breathing;
        public DispatcherTimer? CountdownTimer;
        public DispatcherTimer? ClockTimer;
        public string? LastCountdown;
        // Os avisos pedidos durante a Pausa (ShowToast): aparecem ao voltar.
        public List<(string Message, ToastAction[] Actions)> Toasts { get; } = [];
    }

    private PauseState? _pause;

    // O ecrã calmo. Um Grid não tem par de automação: sem este, o nome «Pausa» nunca chegava ao leitor de ecrã.
    private sealed class PauseSurface : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PauseSurfacePeer(this);

        private sealed class PauseSurfacePeer(PauseSurface owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

            protected override string GetClassNameCore() => nameof(PauseSurface);
        }
    }

    // A mesma Pausa lida no fio do CEF (ShortcutFromPage): nenhuma tecla vai para uma página escondida.
    private volatile bool _pausedForPage;

    // O temporizador, partilhado pelas janelas em Pausa: o fim no relógio monotónico (null = sem fim) e a escolha.
    private static TimeSpan? s_pauseEndsAt;
    private static int s_pauseMinutes;

    private static TimeSpan PauseClock() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static TimeSpan? PauseLeft() => s_pauseEndsAt is { } end ? end - PauseClock() : null;

    private void WatchPause()
    {
        StateChanged += (_, _) =>
        {
            if (_pause is { } state) BreatheWhileSeen(state);
        };
        // Fechar a janela durante a Pausa: os relógios param; o resto vai com a janela.
        Closed += (_, _) =>
        {
            if (_pause is not { } state) return;
            _pause = null;
            _pausedForPage = false;
            StopPauseView(state);
            if (!PausedWindows().Any()) s_pauseEndsAt = null;
        };
    }

    private static List<MainWindow> PausedWindows()
        => Application.Current.Windows.OfType<MainWindow>().Where(w => w._pause is not null).ToList();

    // Ctrl+., a paleta e o menu ⋯: em Pausa volta-se; senão, entram em Pausa todas as janelas abertas.
    private void TogglePause()
    {
        if (_pause is not null) ResumeEverywhere();
        else PauseEverywhere();
    }

    private static void PauseEverywhere()
    {
        var minutes = Settings.Current.PauseMinutes;
        s_pauseMinutes = PauseTimer.IsChoice(minutes) ? minutes : 0;
        s_pauseEndsAt = s_pauseMinutes > 0 ? PauseClock() + TimeSpan.FromMinutes(s_pauseMinutes) : null;
        // Uma janela aberta durante a Pausa não entra nela (só as que estão abertas agora).
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
            if (window.IsLoaded && window._pause is null) window.EnterPause();
    }

    // Esc, Ctrl+., «Voltar»: volta em todas as janelas.
    private static void ResumeEverywhere()
    {
        foreach (var window in PausedWindows()) window.ExitPause();
        s_pauseEndsAt = null;
    }

    // Um link que chega de outra aplicação (MainWindow.Forward.cs) é um gesto do utilizador: acaba a Pausa só na janela que o
    // recebe.
    internal void EndPauseHere()
    {
        ExitPause();
        if (PausedWindows().Count == 0) s_pauseEndsAt = null;
    }

    private bool HandlePauseKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (key == Key.Escape || (ctrl && !shift && !alt && key == Key.OemPeriod))
        {
            ResumeEverywhere();
            return true;
        }
        // Tab, Enter, Espaço e as setas (os chips e «Voltar») e o Alt+F4 seguem o caminho normal do WPF; nenhum atalho do
        // Éteris corre por baixo do ecrã calmo.
        return false;
    }

    // ---------- Entrar ----------

    private void EnterPause()
    {
        if (_pause is not null || !IsLoaded) return;
        // 1. O que estava aberto por cima da página fecha (a camada fica escondida durante a Pausa: OverlayNeeded).
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

        // 2. O plano, de todos os motores vivos da janela: as abas de todos os Espaços com motor (as dos Espaços fora de vista
        // dormem sem motor e não tocam), os motores de vídeo e os sites fixados na barra lateral, abertos ou não.
        var state = new PauseState(PausePlan<FrameworkElement>.From(PauseEngines()));
        _pause = state;
        _pausedForPage = true;

        // 3. Pausar e marcar o que toca (o script só mexe no que está a tocar); 4. calar tudo o que não estava calado, também
        // o som que não vem de <video>/<audio> (Web Audio, jogos) e o dos iframes de outros sites.
        foreach (var engine in state.Plan.Engines.ToList()) _ = MarkForPauseAsync(state, engine);
        foreach (var engine in state.Plan.MutedByPause) SetPauseMute(engine, muted: true);

        // 5. Nenhuma página à vista: a ativa, o 2.º painel do ecrã dividido, o motor de vídeo e as quentes saem pela
        // disposição normal (ShouldShow devolve false: minimizar a janela do Chromium e só depois esconder no WPF); a que
        // ainda segurava uma troca a meio e os sites fixados à parte.
        foreach (var held in _leavingPages.Keys.ToList())
        {
            _leavingPages.Remove(held);
            HidePage(held);
        }
        HideWebPanels();
        LayoutBrowsers();

        // 6. O ecrã calmo na própria janela (as janelas nativas das páginas já estão escondidas).
        ShowPauseView(state);
        UpdateOverlayVisibility();
    }

    private List<PauseEngine<FrameworkElement>> PauseEngines()
    {
        var engines = new List<PauseEngine<FrameworkElement>>();
        foreach (var (tab, browser) in _browsers) engines.Add(new(browser, tab.IsMuted, tab.Media));
        foreach (var (tab, view) in _videoViews) engines.Add(new(view, tab.IsMuted, tab.Media));
        foreach (var browser in _webPanelBrowsers.Values) engines.Add(new(browser, false, MediaState.None));
        return engines;
    }

    private async Task MarkForPauseAsync(PauseState state, FrameworkElement engine)
    {
        var paused = 0;
        try
        {
            if (engine is ChromiumWebBrowser browser)
            {
                var result = await PageScript.EvaluateAsync(browser, MediaInfo.PauseMarkScript, TimeSpan.FromSeconds(2));
                if (result.Success && result.Result is double count) paused = (int)count;
            }
            else if (engine is WebView2 { CoreWebView2: { } core } && await VideoPageScript.EvaluateNumberAsync(core, MediaInfo.PauseMarkScript) is { } count)
                paused = (int)count;
        }
        catch (Exception) { }
        state.Plan.Marked(engine, paused);
    }

    // O silêncio da Pausa nunca passa pelo tab.IsMuted (é o do utilizador): o motor de vídeo lê-o em SetVideoMuted.
    private void SetPauseMute(FrameworkElement engine, bool muted)
    {
        if (engine is ChromiumWebBrowser { IsDisposed: false, IsBrowserInitialized: true } browser)
            browser.GetBrowserHost()?.SetAudioMuted(muted);
        else if (engine is WebView2 view && _videoViews.FirstOrDefault(p => ReferenceEquals(p.Value, view)).Key is { } tab)
            SetVideoMuted(tab);
    }

    // Um motor que fica pronto (ou nasce) durante a Pausa entra calado no plano e recebe o som de volta no regresso.
    private void JoinPause(FrameworkElement engine, bool muted)
    {
        if (_pause is not { } state) return;
        state.Plan.Add(new PauseEngine<FrameworkElement>(engine, muted, MediaState.None));
        if (state.Plan.MutedByPause.Contains(engine)) SetPauseMute(engine, muted: true);
    }

    // ---------- Voltar ----------

    private void ExitPause()
    {
        if (_pause is not { } state) return;
        _pause = null;
        _pausedForPage = false;
        StopPauseView(state);
        if (state.View is { } view) ((Grid)RootBorder.Child).Children.Remove(view);

        // 1. As páginas que estavam à vista, pela disposição normal (à vista no WPF e só depois a janela do Chromium, com o
        // tamanho já dado; depois StackPages), e o site fixado aberto na barra lateral.
        LayoutBrowsers();
        if (_sidebarOpen && CurrentWebPanelBrowser is not null && _sidebarPanel is { } panel) ShowWebPanel(panel, focus: false);

        // 2. O som, só a quem a Pausa o tirou, que ainda existe e que o utilizador não silenciou à mão entretanto.
        var back = state.Plan.Return(PauseEngines());
        foreach (var engine in back.Unmute) SetPauseMute(engine, muted: false);

        // 3. Nada recomeça sozinho; os avisos que ficaram em fila aparecem agora.
        UpdateOverlayVisibility();
        ShowPausedToasts(state);
        ReturnFocusToPage();
        RefreshMediaHub();
    }

    // ---------- Avisos durante a Pausa ----------

    private void QueuePausedToast(string message, ToastAction[] actions) => _pause!.Toasts.Add((message, actions));

    // Os avisos que chegaram durante a Pausa, num só (o lugar do aviso é um): as mensagens por ordem, sem repetir, e as ações
    // dos mais recentes até ao máximo de um aviso.
    private void ShowPausedToasts(PauseState state)
    {
        if (state.Toasts.Count == 0) return;
        var message = string.Join("\n", state.Toasts.Select(t => t.Message).Distinct(StringComparer.Ordinal));
        var actions = state.Toasts.AsEnumerable().Reverse().SelectMany(t => t.Actions).Take(ToastPolicy.MaxActions).ToArray();
        ShowToast(message, actions);
    }

    // ---------- O ecrã calmo ----------

    private void FocusPauseView()
    {
        if (_pause?.Back is not { } back) return;
        if (_overlay is { IsActive: true }) Activate();
        Keyboard.Focus(back);
    }

    private void ShowPauseView(PauseState state)
    {
        var view = BuildPauseView(state);
        state.View = view;
        Grid.SetRow(view, 0);
        Grid.SetRowSpan(view, 3);
        Grid.SetColumnSpan(view, 1);
        Panel.SetZIndex(view, 1000);
        ((Grid)RootBorder.Child).Children.Add(view);
        RefreshPauseTimer(state);
        TickPauseClock(state);
        // «Voltar» com o foco inicial (o foco do Win32 podia ter ficado na página escondida).
        FocusPauseView();
        Dispatcher.InvokeAsync(FocusPauseView, DispatcherPriority.Input);
    }

    private void StopPauseView(PauseState state)
    {
        state.CountdownTimer?.Stop();
        state.ClockTimer?.Stop();
        StopBreathing(state);
    }

    private Grid BuildPauseView(PauseState state)
    {
        var highContrast = SystemParameters.HighContrast;
        var picture = highContrast ? null : PauseBackdrop(Settings.Current.NewTabWallpaper);
        var mini = _miniMode;

        // O texto: branco sobre a paisagem escurecida a 55% (contraste de pelo menos 4,7:1 mesmo sobre um céu branco); sem
        // paisagem, o texto do tema sobre a superfície do tema; em alto contraste, as cores do sistema.
        void Ink(FrameworkElement element, DependencyProperty property)
        {
            if (highContrast) element.SetValue(property, SystemColors.WindowTextBrush);
            else if (picture is not null) element.SetValue(property, Brushes.White);
            else element.SetResourceReference(property, "TextBrush");
        }

        var view = new PauseSurface { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
        AutomationProperties.SetName(view, "Pausa");
        KeyboardNavigation.SetTabNavigation(view, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(view, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(view, KeyboardNavigationMode.Cycle);

        if (highContrast) view.Background = SystemColors.WindowBrush;
        else if (picture is null) view.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
        else
        {
            // Desfocada uma só vez: o desfoque fica numa imagem em cache (BitmapCache), irmã do anel e não mãe dele — um
            // filho animado debaixo de um efeito obrigava a refazer o desfoque em cada fotograma. Maior do que a janela, para
            // as margens do desfoque não ficarem transparentes.
            var image = new Image
            {
                Source = picture,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(-80),
                IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
                CacheMode = new BitmapCache(),
            };
            view.Children.Add(image);
            view.Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = 0.55, IsHitTestVisible = false });
        }

        // Os botões da janela continuam a funcionar (o ecrã calmo tapa a barra de título e a de ferramentas). O resto da
        // faixa de cima continua a servir para arrastar a janela (WindowChrome).
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Height = 34 };
        Button CaptionButton(string glyph, string name, string style, RoutedEventHandler click)
        {
            var button = new Button { Content = glyph, ToolTip = name, Style = (Style)FindResource(style) };
            AutomationProperties.SetName(button, name);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += click;
            caption.Children.Add(button);
            return button;
        }
        CaptionButton("", "Minimizar", "CaptionButton", Minimize_Click);
        var maximize = CaptionButton("", "Maximizar", "CaptionButton", Maximize_Click);
        maximize.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ContentControl.Content)) { Source = MaximizeButton });
        maximize.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(FrameworkElement.ToolTip)) { Source = MaximizeButton });
        CaptionButton("", "Fechar", "CloseCaptionButton", CloseWindow_Click);
        var captionCard = new Border
        {
            Child = caption,
            CornerRadius = new CornerRadius(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (highContrast)
        {
            captionCard.Background = SystemColors.WindowBrush;
            foreach (var button in caption.Children.OfType<Button>()) button.Foreground = SystemColors.WindowTextBrush;
        }
        else captionCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Panel.SetZIndex(captionCard, 1);
        view.Children.Add(captionCard);

        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };

        // O anel íris (160 px; 64 na janela flutuante), com a «Cor do Éteris»; em alto contraste, a cor de realce do sistema.
        var size = mini ? 64.0 : 160.0;
        var scale = new ScaleTransform(1, 1);
        var ring = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = mini ? 2.5 : 4,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            IsHitTestVisible = false,
        };
        var glow = new Ellipse { Width = size, Height = size, Opacity = 0.14, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, IsHitTestVisible = false };
        if (highContrast) ring.Stroke = SystemColors.HighlightBrush;
        else
        {
            ring.SetResourceReference(Shape.StrokeProperty, "IrisBrush");
            glow.SetResourceReference(Shape.FillProperty, "IrisBrush");
        }
        var ringHost = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        if (!highContrast) ringHost.Children.Add(glow);
        ringHost.Children.Add(ring);
        center.Children.Add(ringHost);

        var breath = new TextBlock
        {
            FontSize = mini ? 14 : 20,
            FontWeight = FontWeights.Light,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, mini ? 8 : 20, 0, 0),
            Text = "Respire devagar",
        };
        Ink(breath, TextBlock.ForegroundProperty);
        center.Children.Add(breath);

        // Os chips do temporizador: «Sem fim · 1 min · 5 min · 15 min», o escolhido marcado.
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, mini ? 8 : 26, 0, 0) };
        AutomationProperties.SetName(chips, "Temporizador da Pausa");
        foreach (var minutes in PauseTimer.Choices)
        {
            var choice = minutes;
            var chip = new Button
            {
                Content = PauseTimer.Label(choice),
                Tag = choice,
                Margin = new Thickness(3),
                Padding = mini ? new Thickness(9, 3, 9, 3) : new Thickness(13, 5, 13, 5),
            };
            chip.Click += (_, _) => ChoosePauseMinutes(choice);
            chips.Children.Add(chip);
        }
        center.Children.Add(chips);

        // A contagem: região viva educada, atualizada só quando muda (no máximo de minuto a minuto).
        var countdown = new TextBlock
        {
            FontSize = mini ? 12 : 13.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        Ink(countdown, TextBlock.ForegroundProperty);
        AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Polite);
        center.Children.Add(countdown);

        var clock = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeights.Light,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 26, 0, 0),
            Visibility = mini ? Visibility.Collapsed : Visibility.Visible,
        };
        Ink(clock, TextBlock.ForegroundProperty);
        center.Children.Add(clock);

        var back = new Button
        {
            Content = "Voltar",
            Style = (Style)FindResource("PrimaryPillButton"),
            Padding = mini ? new Thickness(16, 5, 16, 5) : new Thickness(24, 8, 24, 8),
            FontSize = mini ? 12.5 : 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, mini ? 10 : 14, 0, 0),
            ToolTip = "Esc ou Ctrl+.",
        };
        AutomationProperties.SetName(back, "Voltar");
        AutomationProperties.SetAcceleratorKey(back, "Ctrl+.");
        back.Click += (_, _) => ResumeEverywhere();
        center.Children.Add(back);

        var hint = new TextBlock
        {
            Text = "Esc ou Ctrl+. para voltar",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = mini ? Visibility.Collapsed : Visibility.Visible,
        };
        Ink(hint, TextBlock.ForegroundProperty);
        center.Children.Add(hint);

        view.Children.Add(center);

        // Na janela flutuante não há barra de título: o ecrã calmo arrasta-se.
        view.MouseLeftButtonDown += (_, e) =>
        {
            if (_miniMode && e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) DragMove();
        };

        state.Ring = ring;
        state.RingScale = scale;
        state.Breath = breath;
        state.Countdown = countdown;
        state.Clock = clock;
        state.Back = back;
        state.Chips = chips;
        return view;
    }

    // A paisagem da nova aba (Browser/Wallpapers, como Theme/WallpaperAccent.cs), lida uma vez por imagem a 960 px de
    // largura (vai ser desfocada). null sem paisagem («none») ou se a imagem não existir.
    private static readonly Dictionary<string, ImageSource?> s_pauseBackdrops = new(StringComparer.Ordinal);

    private static ImageSource? PauseBackdrop(string wallpaper)
    {
        if (wallpaper is "none" or "") return null;
        if (s_pauseBackdrops.TryGetValue(wallpaper, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.Wallpapers." + wallpaper + ".jpg");
            if (stream is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 960;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception) { }
        return s_pauseBackdrops[wallpaper] = image;
    }

    // ---------- A respiração ----------

    // 0,85 → 1 em 4 s (inspirar) e de volta em 6 s (expirar), a 30 fotogramas por segundo, com «Inspire» e «Expire» no
    // mesmo compasso (as três animações começam no mesmo tique). Com «reduzir movimento» (Theme/Motion.cs) o anel fica
    // parado e o texto diz «Respire devagar».
    private void StartBreathing(PauseState state)
    {
        if (state.Breath is { } words) words.Visibility = Visibility.Visible;
        if (Motion.Reduced || PerfDiagnostics.Without(PerfLayers.Animations))
        {
            if (state.Breath is { } still) still.Text = "Respire devagar";
            return;
        }
        state.Breathes = true;
        BreatheWhileSeen(state);
    }

    // Uma animação eterna só corre enquanto se vê (ForeverAnimationRulesTests): com a janela minimizada sai, e volta a
    // começar (a inspirar) quando a janela se volta a ver.
    private void BreatheWhileSeen(PauseState state)
    {
        var run = state.Breathes && WindowState != WindowState.Minimized;
        if (run == state.Breathing || state.RingScale is not { } scale || state.Breath is not { } words) return;
        state.Breathing = run;
        if (!run)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            words.BeginAnimation(TextBlock.TextProperty, null);
            return;
        }
        var inhale = TimeSpan.FromSeconds(4);
        var cycle = TimeSpan.FromSeconds(10);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var breath = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
        breath.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(inhale), ease));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(cycle), ease));
        Timeline.SetDesiredFrameRate(breath, 30);
        var text = new StringAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
        text.KeyFrames.Add(new DiscreteStringKeyFrame("Inspire", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        text.KeyFrames.Add(new DiscreteStringKeyFrame("Expire", KeyTime.FromTimeSpan(inhale)));
        Timeline.SetDesiredFrameRate(text, 30);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        words.BeginAnimation(TextBlock.TextProperty, text);
    }

    private void StopBreathing(PauseState state)
    {
        state.Breathes = false;
        BreatheWhileSeen(state);
    }

    // ---------- O temporizador e o relógio ----------

    // Um chip escolhido: a contagem recomeça agora em todas as janelas em Pausa, e a escolha fica lembrada (nunca a partir de
    // uma janela privada, que não regista nada).
    private void ChoosePauseMinutes(int minutes)
    {
        if (!PauseTimer.IsChoice(minutes)) return;
        s_pauseMinutes = minutes;
        s_pauseEndsAt = minutes > 0 ? PauseClock() + TimeSpan.FromMinutes(minutes) : null;
        if (!IsPrivate) Settings.Set("pauseMinutes", minutes.ToString(CultureInfo.InvariantCulture));
        foreach (var window in PausedWindows()) window.RefreshPauseTimer(window._pause!);
    }

    private void RefreshPauseTimer(PauseState state)
    {
        var left = PauseLeft();
        if (state.Chips is { } chips)
            foreach (var chip in chips.Children.OfType<Button>())
            {
                var chosen = chip.Tag is int minutes && minutes == s_pauseMinutes;
                chip.Style = (Style)FindResource(chosen ? "PrimaryPillButton" : "PillButton");
                AutomationProperties.SetName(chip, PauseTimer.Label((int)chip.Tag) + (chosen ? ", escolhido" : string.Empty));
            }
        var text = PauseTimer.Remaining(left);
        if (state.Countdown is { } countdown)
        {
            countdown.Text = text ?? string.Empty;
            countdown.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            if (text is not null && text != state.LastCountdown) Sync.LiveRegion.Announce(countdown);
        }
        state.LastCountdown = text;
        // O fim do temporizador: o anel para e aparece «Pronto. Volte quando quiser.» na contagem. A Pausa continua e nada
        // toca.
        if (PauseTimer.Ended(left))
        {
            StopBreathing(state);
            if (state.Breath is { } words) words.Visibility = Visibility.Collapsed;
        }
        else StartBreathing(state);
        state.CountdownTimer?.Stop();
        if (PauseTimer.NextUpdate(left) is not { } next) return;
        if (state.CountdownTimer is null)
        {
            state.CountdownTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            state.CountdownTimer.Tick += (_, _) =>
            {
                state.CountdownTimer.Stop();
                if (ReferenceEquals(_pause, state)) RefreshPauseTimer(state);
            };
        }
        // Um pouco depois da mudança: um relógio que chega adiantado mostrava o minuto anterior outra vez.
        state.CountdownTimer.Interval = next + TimeSpan.FromMilliseconds(50);
        state.CountdownTimer.Start();
    }

    // O relógio (hora:minuto), atualizado na mudança de cada minuto.
    private void TickPauseClock(PauseState state)
    {
        if (state.Clock is not { } clock) return;
        var now = DateTime.Now;
        clock.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (state.ClockTimer is null)
        {
            state.ClockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            state.ClockTimer.Tick += (_, _) =>
            {
                state.ClockTimer.Stop();
                if (ReferenceEquals(_pause, state)) TickPauseClock(state);
            };
        }
        state.ClockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
        state.ClockTimer.Start();
    }
}
