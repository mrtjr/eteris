using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Palette;
using Eteris.Browser.Shields;
using Eteris.Browser.Sidebar;
using Eteris.Browser.Tabs;
using Eteris.Browser.Theme;

namespace Eteris.Browser;

public partial class MainWindow
{
    private const string ThemeColorScript = """
        (function () {
          var m = document.querySelector('meta[name="theme-color"][media*="dark"]')
               || document.querySelector('meta[name="theme-color"]:not([media])')
               || document.querySelector('meta[name="theme-color"]');
          if (m && m.content) return m.content;
          var b = getComputedStyle(document.body || document.documentElement).backgroundColor;
          if (!b || b === 'rgba(0, 0, 0, 0)') b = getComputedStyle(document.documentElement).backgroundColor;
          return b;
        })()
        """;

    private bool _focusMode;
    private double _pageRadius = 10;

    private bool PaletteOpen => PaletteOverlay.Visibility == Visibility.Visible;

    // ---------- Paleta de comandos ----------

    private void OpenPalette()
    {

        CloseBrowserMenu();
        CloseRadar();
        CloseSuggestions();
        ShieldPopup.IsOpen = false;
        PaletteOverlay.Visibility = Visibility.Visible;
        BrowserHost.Effect = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance };
        PaletteInput.Text = string.Empty;
        RefreshPalette();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PaletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PaletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PaletteCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        FocusInOverlay(PaletteInput);
    }

    private void ClosePalette()
    {
        if (!PaletteOpen) return;
        PaletteOverlay.Visibility = Visibility.Collapsed;
        BrowserHost.Effect = null;
        ReturnFocusToPage();
    }

    private void TogglePalette()
    {
        if (PaletteOpen) ClosePalette();
        else OpenPalette();
    }

    private IEnumerable<PaletteCommand> Actions()
    {
        var tab = Tabs.Active;
        var address = tab?.Address ?? string.Empty;
        var host = WebHost(address);
        var zoom = ZoomSteps.Percent(ActiveBrowser?.ZoomLevel ?? 0);

        yield return new("", "Nova aba", "Abrir a página inicial do Éteris", "Ctrl+T", OpenNewTab);
        yield return new("\uE71D", "Radar de abas", "Encontre, compare ou reabra abas desta janela", "Ctrl+Shift+A", ToggleRadar);
        if (s_closedWindows.Latest is { } closedWindow)
            yield return new("\uE8A7", $"Reabrir janela fechada ({closedWindow.Addresses.Count} abas)",
                closedWindow.Titles.FirstOrDefault() ?? string.Empty, null, ReopenClosedWindow);
        if (Tabs.Tabs.Count > 1)
        {
            yield return new("\uE8C8", "Fechar abas duplicadas", "Deixa uma aba por endereço", null, () =>
            {
                var closed = Tabs.CloseDuplicates();
                ShowToast(closed == 0 ? "Não havia abas duplicadas" : closed == 1 ? "1 aba duplicada fechada" : $"{closed} abas duplicadas fechadas");
            });
            yield return new("\uE8CB", "Ordenar abas por site", "Junta as abas do mesmo site", null, () => Tabs.SortBySite());
        }
        yield return new("", "Reabrir aba fechada", Tabs.RecentlyClosed.FirstOrDefault() is { } last ? $"«{last.Title}»" : "Nenhuma aba fechada nesta sessão", "Ctrl+Shift+T", () => Tabs.ReopenClosed());
        foreach (var closed in Tabs.RecentlyClosed.Skip(1).Take(5))
        {
            var entry = closed;
            yield return new("", $"Reabrir «{entry.Title}»", entry.Address, null, () => Tabs.ReopenClosed(entry));
        }
        foreach (var command in SessionCommands()) yield return command;
        yield return new("", "Esquecer os últimos 15 minutos", "Apaga o histórico desse período e as abas fechadas recentes", null, () => Forget(15));
        yield return new("", "Esquecer a última hora", "Apaga o histórico da última hora e as abas fechadas recentes", null, () => Forget(60));
        if (Tabs.Tabs.Any(t => t.Media == Eteris.Browser.Tabs.MediaState.Playing))
            yield return new("\uE769", "Pausar tudo o que está a tocar", "Vídeos e áudio de todas as abas", null, PauseAllMedia);
        if (Tabs.Tabs.Any(t => !ReferenceEquals(t, Tabs.Active) && !t.IsMuted && t.Media != Eteris.Browser.Tabs.MediaState.None))
            yield return new("\uE74F", "Silenciar outras abas", "Fica só o som desta aba", null, MuteOtherTabs);
        yield return new("", "Adormecer abas em segundo plano", "Liberta a memória das abas que não está a usar", null,
            SleepAllBackgroundTabs);
        foreach (var command in WorkspaceCommands()) yield return command;
        yield return new("", "Nova janela", "Abrir outra janela do Éteris", "Ctrl+N", () => OpenWindow(isPrivate: false));
        yield return new("", "Nova janela privada", "Nada fica no histórico, nos cookies nem na cache", "Ctrl+Shift+N",
            () => OpenWindow(isPrivate: true));
        if (SelectedTabs is { Count: >= 2 } selected)
        {
            var count = TabSelection.Count(selected.Count);
            yield return new("", $"Copiar {selected.Count} links", "As abas selecionadas, como texto e HTML", null, () => CopySelectedLinks(selected));
            yield return new("", $"Agrupar {count} num novo grupo", "As abas selecionadas ficam juntas", null, () => GroupSelected(selected));
            yield return new("", $"Adormecer {count}", "Liberta a memória das abas selecionadas", null, () => SleepSelected(selected));
            yield return new("", $"Fechar {count}", "As abas selecionadas (trancadas ficam)", null, () => CloseSelected(selected));
            yield return new("", "Limpar a seleção", "Ctrl+clique e Shift+clique selecionam abas", null, ClearSelection);
        }
        if (tab is not null)
        {
            yield return new("", "Mudar o nome da aba…", tab.CustomTitle is null ? "Nome próprio, emoji e cor para esta aba" : $"Agora «{tab.CustomTitle}»", null,
                () => RenameTab(tab, Omnibox));
            yield return new("", tab.HasNote ? "Editar a nota da aba…" : "Escrever nota na aba…",
                tab.Note ?? "«Porque abri isto?» — fica no cartão da aba, no Radar e nas sessões", null, () => RenameTab(tab, Omnibox, focusNote: true));
            yield return new(tab.IsLocked ? "" : "", tab.IsLocked ? "Destrancar aba" : "Trancar aba",
                tab.IsLocked ? "Volta a poder fechar-se e adormecer" : "Não fecha por engano nem adormece", null, () => tab.IsLocked = !tab.IsLocked);
            yield return new("", "Feito", IsPrivate ? "Fecha a aba" : "Fecha a aba e guarda-a em «Feitas hoje» (Radar)", null, () => MarkTabDone(tab));
            if (!IsPrivate && !NewTabPage.IsNewTab(address))
                yield return new("", "Adiar aba até…", "A aba sai da faixa e volta à hora que escolher, com aviso", null, () => OpenSnoozeMenu(tab, Omnibox));
            yield return new("", "Fechar aba", tab.Title, "Ctrl+W", () => CloseTab(tab));
            yield return new("", "Duplicar aba", tab.Title, null, () => Tabs.NewTab(address));
            yield return new("", "Recarregar página", tab.Title, "F5", () => ReloadActive());
            yield return new("", "Procurar na página", tab.Title, "Ctrl+F", OpenFind);
            yield return new("", AppData.IsBookmarked(address) ? "Remover dos favoritos" : "Adicionar aos favoritos",
                tab.Title, "Ctrl+D", ToggleBookmark);
            yield return new("", "Transferências", "Ver transferências desta sessão", "Ctrl+J", ToggleDownloads);
        }
        if (host is not null)
        {
            if (Tabs.Active is { } qrTab && ReaderScripts.IsCandidate(qrTab.Address))
                yield return new("\uED14", "Código QR desta página", "Para abrir no telemóvel — feito neste computador", null, ShowQrCode);
            var cleanAddress = Shields.UrlCleaner.Clean(address);
            var pageTitle = Tabs.Active?.Title ?? cleanAddress;
            yield return new("", "Copiar endereço", cleanAddress == address ? address : cleanAddress + "  (sem rastreio)", null,
                () => CopyText(cleanAddress, cleanAddress == address ? "Endereço copiado" : "Endereço copiado sem parâmetros de rastreio"));
            yield return new("", "Copiar como link Markdown", Browser.LinkFormats.Markdown(pageTitle, cleanAddress), null,
                () => CopyText(Browser.LinkFormats.Markdown(pageTitle, cleanAddress), "Link Markdown copiado"));
            yield return new("", "Copiar título e endereço", "Para colar numa mensagem", null,
                () => CopyText(Browser.LinkFormats.TitleAndAddress(pageTitle, cleanAddress), "Título e endereço copiados"));
            var on = AppData.IsShieldOn(host);
            yield return new("", on ? "Desligar o escudo neste site" : "Ligar o escudo neste site",
                $"{TrackerBlocker.RegistrableDomain(host)} · {tab!.BlockedCount} bloqueados nesta página", null, () =>
                {
                    AppData.SetShield(host, !on);
                    ReloadActive();
                    UpdateShieldButton();
                });
        }
        yield return new("", "Capturar página inteira", "Guarda em Imagens › Éteris e copia a imagem", "Ctrl+Shift+S", () => CapturePage(true));
        yield return new("", "Guardar como PDF", "Guarda a página na pasta de transferências", null, SaveAsPdf);
        yield return new("", "Capturar área visível", "Só o que está no ecrã", null, () => CapturePage(false));
        if (WebHost(Tabs.Active?.Address) is { } pickHost)
        {
            yield return new("\uE7BC", AppData.IsSavedForLater(Tabs.Active!.Address) ? "Já está na lista de leitura" : "Ler mais tarde",
                "Guarda esta página na lista «Para ler»", null, SaveForLater);
            if (!IsPrivate && Tabs.Active is { } darkTab && Shields.DarkPages.IsCandidate(darkTab.Address) && WebHost(darkTab.Address) is { } darkSite)
            {
                var dark = AppData.IsDark(darkSite);
                yield return new("\uE793", dark ? "Repor as cores deste site" : "Escurecer este site",
                    dark ? $"Deixa de escurecer {darkSite}" : $"Modo escuro para {darkSite}, lembrado da próxima vez", null, () => ToggleDarkSite(darkSite, !dark));
            }
            yield return new("", "Esconder um elemento desta página", "Clique num anúncio, banner ou caixa para o fazer desaparecer neste site", null, PickElementToHide);
            if (AppData.HiddenElements(pickHost).Count is > 0 and var hiddenCount)
                yield return new("", "Repor elementos escondidos", $"{hiddenCount} escondido(s) em {pickHost}", null, ResetHiddenElements);
        }
        yield return new("", "Seguir um link pelo teclado", "Letras sobre cada link; escreva-as para o abrir", "Ctrl+Shift+L", ShowLinkHints);
        if (Tabs.Active?.IsReaderable == true)
            yield return new("", "Modo de leitura", "Só o artigo, sem anúncios nem distrações", "F9", ToggleReader);
        if (IsSplit)
        {
            yield return new("", "Trocar lados", "A página da esquerda passa para a direita (ou a de cima para baixo)", null, SwapSplit);
            yield return new("", _split!.IsStacked ? "Lado a lado" : "Empilhar", _split.IsStacked ? "As duas páginas lado a lado" : "Uma página em cima da outra", null, ToggleSplitOrientation);
            yield return new("", "Fechar ecrã dividido", "Voltar a uma página de cada vez", null, CloseSplit);
        }
        else if (SplitCandidate() is { } partner)
            yield return new("", "Dividir ecrã", $"Mostrar «{partner.Title}» ao lado", null, () => OpenSideBySide(partner));
        var light = Theme.ThemeManager.Current == Theme.AppTheme.Light;
        yield return new(light ? "" : "", light ? "Mudar para tema escuro" : "Mudar para tema claro",
            "Também pode seguir o Windows em Definições › Aspeto", null, () => Data.Settings.Set("theme", light ? "dark" : "light"));
        yield return new("", VerticalTabs ? "Abas no topo" : "Abas verticais",
            VerticalTabs ? "Voltar à faixa de abas clássica" : "Lista de abas na lateral, ideal para muitas abas", null, ToggleVerticalTabs);
        if (!VerticalTabs)
            yield return new("", CompactDensity ? "Sair do modo compacto" : "Modo compacto",
                CompactDensity ? "Abas e endereço voltam a ter cada um a sua barra" : "Abas e endereço numa só linha: a página ganha uma barra inteira", null, ToggleCompactDensity);
        yield return new("", "Atalhos de teclado", "Todos os atalhos do Éteris numa só vista", "F1", ToggleShortcuts);
        yield return new("\uE789", "Novidades desta versão", "O que mudou no Éteris", null, () => OpenInternal(WhatsNewPage.Url));
        yield return new("", "Definições", "Motor de pesquisa, arranque, abas e privacidade", "Ctrl+,", OpenSettings);
        yield return new("\uE790", "Cor do Éteris", "Escolher a cor de destaque: paleta, da paisagem ou do Windows", null,
            () => OpenInternal(SettingsPage.Url + "#aspeto"));
        yield return new("\uE8A0", SidebarOpen ? "Fechar a barra lateral" : "Barra lateral",
            SidebarOpen ? "A página volta a ter a largura toda" : "Notas, Para ler, Favoritos e Histórico ao lado da página", "Ctrl+Shift+B", () => ToggleSidebar());
        foreach (var panel in SidebarAvailable)
        {
            var key = panel.Key;
            yield return new(panel.Glyph, panel.Key == SidebarPanels.Notes ? "Notas" : $"{panel.Title} na barra lateral",
                panel.Key switch
                {
                    SidebarPanels.Notes => "Bloco de notas ao lado da página, guardado só neste computador",
                    SidebarPanels.Reading => "As páginas guardadas para ler mais tarde, ao lado da página",
                    SidebarPanels.Bookmarks => "Os favoritos ao lado da página",
                    SidebarPanels.History => "As páginas visitadas ao lado da página",
                    _ => $"Site fixado · {SidebarLists.HostOf(panel.Url ?? string.Empty)}",
                }, panel.Key == SidebarPanels.Notes ? "Ctrl+Shift+E" : null, () => OpenSidebar(key));
        }
        if (SidebarOpen && _sidebarPanel?.Url is { } shownSite && WebPanels.Find(_webPanels, shownSite) is { } pinned)
        {
            // O que o menu do separador faz, sem rato: mover o site na coluna e a vista móvel.
            var siteHost = SidebarLists.HostOf(shownSite);
            var index = WebPanels.IndexOf(_webPanels, shownSite);
            if (index > 0)
                yield return new("\uE74A", $"Mover «{siteHost}» para cima na barra lateral", "Sobe um lugar na coluna dos separadores", null,
                    () => MovePinnedSite(shownSite, -1));
            if (index < _webPanels.Count - 1)
                yield return new("\uE74B", $"Mover «{siteHost}» para baixo na barra lateral", "Desce um lugar na coluna dos separadores", null,
                    () => MovePinnedSite(shownSite, 1));
            yield return new(pinned.Mobile ? "\uE73E" : "\uE8EA", pinned.Mobile ? $"Desligar a vista móvel de «{siteHost}»" : $"Vista móvel de «{siteHost}»",
                pinned.Mobile ? "O site volta a ver um computador" : "O site passa a ver um telemóvel (versão estreita)", null,
                () => SetPinnedSiteMobile(shownSite, !pinned.Mobile));
        }
        if (SidebarOpen)
        {
            var onLeft = Settings.Current.SidebarSide == "left";
            yield return new(onLeft ? "\uE8A0" : "\uE89F", onLeft ? "Barra lateral à direita" : "Barra lateral à esquerda",
                "Muda a coluna de lado (fica lembrado)", null, () => Settings.Set("sidebarSide", onLeft ? "right" : "left"));
        }
        if (!IsPrivate && host is not null)
            yield return new(WebPanels.Glyph,
                IsPinnedSite(address) ? "Tirar este site da barra lateral" : "Fixar este site na barra lateral",
                IsPinnedSite(address) ? $"{host} deixa de abrir dentro da barra" : $"{host} passa a abrir dentro da barra, com os cookies e o Escudo de sempre",
                null, () =>
                {
                    if (IsPinnedSite(address)) UnpinSite(WebPanels.Normalize(address)!);
                    else PinCurrentSite();
                });
        yield return new("\uE7BC", "Lista de leitura", "Páginas guardadas para ler mais tarde", null, () => OpenInternal(BookmarksPage.Url + "#ler"));
        yield return new("\uEDE1", "Exportar favoritos", "Ficheiro HTML em Transferências, que qualquer navegador importa", null, ExportBookmarks);
        yield return new("", "Favoritos", "Ver, pesquisar, renomear e remover favoritos", "Ctrl+Shift+O", OpenBookmarks);
        yield return new("", "Histórico", "Ver, pesquisar e apagar páginas visitadas", "Ctrl+H", OpenHistory);
        yield return new("", "Aumentar zoom", $"Atual: {zoom}%", "Ctrl +", () => Zoom(1));
        yield return new("", "Reduzir zoom", $"Atual: {zoom}%", "Ctrl −", () => Zoom(-1));
        yield return new("", "Repor zoom", "Voltar a 100%", "Ctrl+0", () => Zoom(0));
        yield return new("\uE8A7", _miniMode ? "Sair da janela flutuante" : "Janela flutuante",
            _miniMode ? "Voltar ao tamanho e posição de antes" : "Pequena, num canto e sempre por cima — para ver um vídeo enquanto trabalha", "Ctrl+Shift+M", ToggleMiniWindow);
        if (_pageFullscreen)
            yield return new("", "Sair do ecrã inteiro", "A janela volta ao tamanho normal", "Esc", ExitPageFullscreen);
        else
            yield return new("", _focusMode ? "Sair do modo foco" : "Modo foco",
                "Esconde abas e barras para ler sem distrações", "F11", ToggleFocusMode);
    }

    private void RefreshPalette()
    {
        var query = PaletteInput.Text.Trim();
        PalettePlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var tabs = Tabs.Tabs
            .Where(t => !ReferenceEquals(t, Tabs.Active))
            .Select(t => new PaletteCommand("", t.DisplayTitle, "Mudar para esta aba · " + (WebHost(t.Address) ?? "Éteris"), null,
                () => Tabs.Active = t));

        // Ao escrever, também as abas dos outros Espaços desta janela («Ir» muda de Espaço e ativa a aba)…
        var otherSpaces = query.Length == 0 ? [] : Tabs.TabsInOtherWorkspaces()
            .Select(pair => new PaletteCommand("", pair.Tab.DisplayTitle,
                $"Mudar para esta aba · Espaço «{pair.Space.Name}» · " + (WebHost(pair.Tab.Address) ?? "Éteris"), null, () =>
                {
                    SwitchWorkspace(pair.Space);
                    if (Tabs.Tabs.Contains(pair.Tab)) Tabs.Active = pair.Tab;
                }))
            .ToList();

        // …e as das outras janelas (só do mesmo tipo: privadas com privadas).
        var otherWindows = query.Length == 0 ? [] : Application.Current.Windows.OfType<MainWindow>()
            .Where(w => !ReferenceEquals(w, this) && w.IsPrivate == IsPrivate)
            .SelectMany(w => w.Tabs.Tabs.Select(t => new PaletteCommand("", t.DisplayTitle,
                "Mudar para esta aba · outra janela · " + (WebHost(t.Address) ?? "Éteris"), null, () => FocusTabIn(w, t))))
            .ToList();

        var items = CommandSearch.RankTitled(query, Actions().Concat(tabs).Concat(otherSpaces).Concat(otherWindows), c => c.Title, c => c.Subtitle);

        if (query.Length >= 2)
            items.AddRange(AppData.Bookmarks(b => b.Search(query, 4)).Select(b =>
                new PaletteCommand("", b.Title.Length > 0 ? b.Title : b.Url, "Favorito · " + b.Url, null, () => LoadUrl(b.Url))));

        if (query.Length >= 2)
            items.AddRange(AppData.TryRead(h => h.Search(query, 5)).Select(h =>
                new PaletteCommand("", h.Title.Length > 0 ? h.Title : h.Url, h.Url, null, () => LoadUrl(h.Url))));

        if (query.Length > 0 && OmniboxClassifier.Classify(query, Engine) is { } result)
        {
            var open = new PaletteCommand(result.IsSearch ? "" : "",
                result.IsSearch ? $"Pesquisar «{result.Query}»" : $"Abrir {query}",
                result.IsSearch ? $"com {(result.Engine ?? Engine).Name}" : result.Url, null, () => LoadUrl(result.Url));
            if (result.IsSearch) items.Add(open);
            else items.Insert(0, open);
        }

        PaletteList.ItemsSource = items.Take(12).ToList();
        PaletteList.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void ToggleDarkSite(string host, bool on)
    {
        AppData.SetDark(host, on);
        if (ActiveBrowser is { } browser) PageScript.Run(browser, Shields.DarkPages.Live(on));
        ShowToast(on ? $"{host} fica escuro — também da próxima vez" : $"{host} volta às cores originais");
    }

    private static void FocusTabIn(MainWindow window, Tabs.BrowserTab tab)
    {
        if (!window.Tabs.Tabs.Contains(tab)) return;
        window.Tabs.Active = tab;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void RunPaletteCommand(PaletteCommand command)
    {
        ClosePalette();
        command.Run();
    }

    private void PaletteInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshPalette();

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = PaletteList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + count) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (PaletteList.SelectedItem is PaletteCommand command) RunPaletteCommand(command);
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePalette();
                e.Handled = true;
                break;
        }
    }

    private void PaletteItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PaletteCommand command) RunPaletteCommand(command);
        e.Handled = true;
    }

    private void PaletteBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ClosePalette();

    private void Menu_Click(object sender, RoutedEventArgs e) => ToggleBrowserMenu();

    // ---------- Esquecer ----------

    private void Forget(int minutes)
    {
        AppData.TryWrite(h => h.DeleteSince(DateTime.UtcNow.AddMinutes(-minutes)));
        Tabs.ForgetClosed();
        ShowToast(minutes < 60 ? $"Os últimos {minutes} minutos foram esquecidos" : "A última hora foi esquecida");
    }

    // ---------- Modo foco ----------

    private void ToggleFocusMode()
    {
        if (_pageFullscreen)
        {
            ExitPageFullscreen();
            return;
        }

        _focusMode = !_focusMode;
        CloseSuggestions();
        ShieldPopup.IsOpen = false;
        // O teclado vai para a página: com o foco na barra de endereço as barras não se esconderiam (ver BarsInUse).
        if (_focusMode && Omnibox.IsKeyboardFocusWithin) ReturnFocusToPage();
        ApplyChromeLayout();

        if (!_focusMode) return;
        var toast = new DoubleAnimationUsingKeyFrames();
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        FocusToast.BeginAnimation(OpacityProperty, toast);
    }

    // ---------- Íris adaptativa: a interface ganha o tom do site ----------

    private void DetectSiteTint(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)
    {
        if (NewTabPage.IsNewTab(url))
        {
            tab.Tint = null;
            return;
        }

        PageScript.EvaluateAsync(browser, ThemeColorScript).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result.Success && task.Result.Result is string css)
                Dispatcher.InvokeAsync(() => tab.Tint = SiteTint.Parse(css) is { } color ? SiteTint.ForChrome(color) : null);
        });
    }

    private void ApplyTint()
    {
        // As cores dos sites estão afinadas para o tema escuro; no claro a superfície fica neutra.
        var tint = UsesLight ? LightChrome : Tabs.Active?.Tint ?? SiteTint.Base;
        // Com Espaços, a barra ganha uma pitada da cor do Espaço à vista (Íris adaptativa).
        if (Tabs.HasWorkspaces) tint = SiteTint.WithWorkspace(tint, Tabs.Current.Color, UsesLight);
        // Com Mica, a superfície das barras fica translúcida para o material do sistema transparecer.
        var target = _mica ? Color.FromArgb(ChromeAlpha, tint.R, tint.G, tint.B) : tint;
        var brush = (SolidColorBrush)Resources["ChromeSurfaceBrush"];
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }
}
