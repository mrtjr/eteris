using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser;

// Barra lateral unificada (Ctrl+Shift+B): uma coluna ao lado da página, à esquerda ou à direita, redimensionável
// pela pega junto à página, com os separadores na borda de fora. A página encolhe em vez de ser tapada, por isso a
// barra vive na janela principal; só o que for desenhado por cima dela (avisos) vai para a camada de sobreposição.
// A lógica (painéis disponíveis, larguras, margens) está em Sidebar/SidebarPanels.cs.
public partial class MainWindow
{
    private bool _sidebarOpen;
    private SidebarPanel? _sidebarPanel;
    private double _sidebarWidth = SidebarPanels.DefaultWidth;

    // O lado é uma preferência global (todas as janelas acompanham, pelo Settings.Changed → ApplyChromeLayout).
    private static SidebarSide SidebarSideNow => SidebarPanels.ParseSide(Settings.Current.SidebarSide);
    private readonly Dictionary<string, Button> _railButtons = new(StringComparer.Ordinal);

    private bool SidebarOpen => _sidebarOpen;

    private bool SidebarShows(string key) => _sidebarOpen && _sidebarPanel?.Key == key;

    // Os painéis desta janela: os embutidos (menos os que não cabem na janela privada) e os sites fixados.
    private IReadOnlyList<SidebarPanel> SidebarAvailable => [.. SidebarPanels.Available(IsPrivate), .. WebSidebarPanels];

    private void WatchSidebar()
    {
        var settings = Settings.Current;
        _sidebarWidth = settings.SidebarWidth;
        LoadWebPanels();
        BuildSidebarRail();
        // Aberta como ficou da última vez (nunca na janela privada: começa fechada, como o resto do que é lembrado).
        if (settings.Sidebar && !IsPrivate && SidebarPanels.Resolve(settings.SidebarPanel, SidebarAvailable) is { } panel)
        {
            _sidebarOpen = true;
            // A disposição das barras já correu (WatchTabLayoutSetting): a coluna é posta agora no seu lugar.
            PlaceSidebar();
            ShowSidebarPanel(panel, focus: false);
        }
        SizeChanged += (_, _) =>
        {
            // A janela encolheu: a barra nunca fica com mais de metade.
            var clamped = SidebarPanels.ClampWidth(_sidebarWidth, ActualWidth);
            if (Math.Abs(clamped - _sidebarWidth) > 0.5)
            {
                _sidebarWidth = clamped;
                PlaceSidebar();
            }
        };
    }

    private void ToggleSidebar(string? panelKey = null)
    {
        if (_sidebarOpen && (panelKey is null || _sidebarPanel?.Key == panelKey)) CloseSidebar();
        else OpenSidebar(panelKey);
    }

    private void OpenSidebar(string? panelKey = null)
    {
        var panel = SidebarPanels.Resolve(panelKey ?? Settings.Current.SidebarPanel, SidebarAvailable);
        if (panel is null)
        {
            ShowToast("A barra lateral não tem painéis na janela privada");
            return;
        }
        var wasOpen = _sidebarOpen;
        _sidebarOpen = true;
        // A coluna tem de estar à vista antes de o painel pedir o teclado (um elemento escondido não recebe o foco).
        PlaceSidebar();
        ShowSidebarPanel(panel, focus: true);
        if (!IsPrivate)
        {
            Settings.Set("sidebar", "true");
            Settings.Set("sidebarPanel", panel.Key);
        }
        if (!wasOpen) SidebarCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void CloseSidebar()
    {
        if (!_sidebarOpen) return;
        FlushNotes();
        _sidebarOpen = false;
        // Os sites fixados adormecem com a barra fechada: largam a memória e voltam a carregar ao abrir.
        ReleaseWebPanelBrowsers();
        if (!IsPrivate) Settings.Set("sidebar", "false");
        PlaceSidebar();
        ReturnFocusToPage();
    }

    // Único sítio que põe a coluna no seu lugar: lado, largura, pega e separadores, e as margens da página.
    // Chamado por ApplyChromeLayout (modo foco e ecrã inteiro escondem-na) e ao arrastar a pega.
    private void PlaceSidebar()
    {
        var visible = _sidebarOpen && _chrome.Mode != Theme.ChromeMode.Hidden;
        var side = SidebarSideNow;
        var left = side == SidebarSide.Left;
        var sideTabs = SideBar.Visibility == Visibility.Visible ? SideBar.Width : 0;
        var margins = SidebarPanels.PageMargins(sideTabs, visible, side, _sidebarWidth);

        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Width = _sidebarWidth;
        Sidebar.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Sidebar.Margin = new Thickness(left ? margins.Left - (_sidebarWidth - SidebarPanels.Gap) : 0, 0, 0, 0);
        // À direita: [pega][cartão][separadores]; à esquerda, o espelho.
        SidebarStartColumn.Width = new GridLength(left ? SidebarPanels.RailWidth : SidebarPanels.Gap);
        SidebarEndColumn.Width = new GridLength(left ? SidebarPanels.Gap : SidebarPanels.RailWidth);
        Grid.SetColumn(SidebarGrip, left ? 2 : 0);
        Grid.SetColumn(SidebarRail, left ? 0 : 2);
        foreach (var button in _railButtons.Values) button.HorizontalContentAlignment = HorizontalAlignment.Center;

        PageFrame.Margin = new Thickness(margins.Left, 0, margins.Right, 0);
        // O que flutua sobre a página (procurar, pedidos de permissão, avisos, destino do link) segue a moldura da
        // página, não a janela: os avisos ficam centrados sobre a página e nunca por cima do painel.
        FindBar.Margin = new Thickness(0, 12, 24 + margins.Right, 0);
        PermissionBar.Margin = new Thickness(20 + margins.Left, 10, 0, 0);
        Toast.Margin = new Thickness(margins.Left, 0, margins.Right, 28);
        PlaceSignInBar(new Thickness(margins.Left, 0, margins.Right, 0));
        SetLinkBubbleOffset(new Thickness(12 + margins.Left, 0, 0, 12));
        SyncOverlayBounds();
    }

    private void ShowSidebarPanel(SidebarPanel panel, bool focus)
    {
        if (_sidebarPanel is { Key: SidebarPanels.Notes } && panel.Key != SidebarPanels.Notes) FlushNotes();
        _sidebarPanel = panel;
        SidebarTitle.Text = panel.Title;
        foreach (var (key, button) in _railButtons) button.Tag = key == panel.Key ? "on" : null;

        SidebarNotes.Visibility = panel.Key == SidebarPanels.Notes ? Visibility.Visible : Visibility.Collapsed;
        NotesInsertButton.Visibility = SidebarNotes.Visibility;
        SidebarLinks.Visibility = panel.Key is SidebarPanels.Reading or SidebarPanels.Bookmarks or SidebarPanels.History ? Visibility.Visible : Visibility.Collapsed;
        SidebarWebHost.Visibility = panel.IsWeb ? Visibility.Visible : Visibility.Collapsed;
        WebPanelOpenButton.Visibility = WebPanelReloadButton.Visibility = SidebarWebHost.Visibility;
        if (panel.IsWeb)
        {
            ShowWebPanel(panel, focus);
            return;
        }
        HideWebPanels();

        switch (panel.Key)
        {
            case SidebarPanels.Notes:
                LoadNotes();
                if (focus) NotesText.Focus();
                break;
            case SidebarPanels.Reading:
            case SidebarPanels.Bookmarks:
            case SidebarPanels.History:
                SidebarFilterHint.Text = panel.Key switch
                {
                    SidebarPanels.Reading => "Filtrar «Para ler»",
                    SidebarPanels.Bookmarks => "Filtrar favoritos",
                    _ => "Filtrar o histórico",
                };
                if (SidebarFilter.Text.Length > 0) SidebarFilter.Clear();
                else RefreshLinksPanel();
                if (focus) SidebarFilter.Focus();
                break;
        }
    }

    // ---------- Para ler, Favoritos e Histórico ----------

    // Sem filtro, as mais recentes; com filtro, a procura vai ao histórico e aos favoritos inteiros (não só às
    // últimas 400 visitas / 500 favoritos, que com uso normal são um ou dois dias de histórico).
    private const int SidebarHistoryRows = 400;
    private const int SidebarBookmarkRows = 500;

    // A linha escolhida com as setas (índice em SidebarLinkList; -1 = nenhuma) e, ao tirar uma linha pelo teclado,
    // manter a escolha no mesmo sítio.
    private int _sidebarCursor = -1;
    private bool _keepSidebarCursor;

    private IReadOnlyList<object> SidebarRows => SidebarLinkList.ItemsSource as IReadOnlyList<object> ?? [];

    private void RefreshLinksPanel()
    {
        if (_sidebarPanel is not { } panel) return;
        var query = SidebarFilter.Text.Trim();
        SidebarFilterHint.Visibility = SidebarFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var previous = _sidebarCursor;
        IReadOnlyList<object> rows = panel.Key switch
        {
            SidebarPanels.Reading => SidebarLists.Reading(AppData.ReadingList(), DateTime.UtcNow, query),
            SidebarPanels.Bookmarks => SidebarLists.Bookmarks(AppData.Bookmarks(b => b.All(query.Length > 0 ? int.MaxValue : SidebarBookmarkRows)), query),
            SidebarPanels.History => SidebarLists.History(AppData.TryRead(h => query.Length > 0
                ? h.VisitsMatching((title, url) => SidebarLists.Matches(title, url, query), SidebarHistoryRows)
                : h.Visits(SidebarHistoryRows)), DateTime.Now, query),
            _ => [],
        };
        ApplySiteIcons(rows.OfType<SidebarLink>());
        SidebarLinkList.ItemsSource = rows;
        _sidebarCursor = -1;
        if (_keepSidebarCursor && previous >= 0 && rows.Count > 0)
        {
            var at = Math.Min(previous, rows.Count - 1);
            SetSidebarCursor(rows[at] is SidebarLink ? at : SidebarLists.MoveCursor(rows, at, 1) is var next && next != at ? next : SidebarLists.MoveCursor(rows, at, -1));
        }
        SidebarLinksEmpty.Text = query.Length > 0 ? $"Nada encontrado para «{query}»."
            : panel.Key switch
            {
                SidebarPanels.Reading => "Nada para ler mais tarde. «Ler mais tarde» no menu da página, na paleta ou ao espreitar um link guarda a página aqui.",
                SidebarPanels.Bookmarks => "Ainda não há favoritos. A estrela na barra de endereço (Ctrl+D) guarda a página aberta.",
                _ => "O histórico está vazio.",
            };
        SidebarLinksEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSidebarCursor(int index)
    {
        var rows = SidebarRows;
        if (_sidebarCursor >= 0 && _sidebarCursor < rows.Count && rows[_sidebarCursor] is SidebarLink old) old.IsCurrent = false;
        _sidebarCursor = index >= 0 && index < rows.Count && rows[index] is SidebarLink ? index : -1;
        if (_sidebarCursor < 0) return;
        ((SidebarLink)rows[_sidebarCursor]).IsCurrent = true;
        (SidebarLinkList.ItemContainerGenerator.ContainerFromIndex(_sidebarCursor) as FrameworkElement)?.BringIntoView();
    }

    private SidebarLink? SidebarCurrentLink
        => _sidebarCursor >= 0 && _sidebarCursor < SidebarRows.Count ? SidebarRows[_sidebarCursor] as SidebarLink : null;

    // Ícones dos sites já guardados pelo histórico (os mesmos de eteris://history): descodificados uma vez por janela.
    private readonly Dictionary<string, ImageSource?> _siteIconCache = new(StringComparer.Ordinal);

    private void ApplySiteIcons(IEnumerable<SidebarLink> links)
    {
        if (IsPrivate) return;
        var pending = links.Where(link => HistoryStore.SiteIconKey(link.Url) is { } site && !_siteIconCache.ContainsKey(site)).ToList();
        if (pending.Count > 0)
        {
            foreach (var (site, dataUri) in AppData.SiteIcons(pending.Select(l => l.Url)))
                _siteIconCache[site] = DecodeDataUri(dataUri);
            foreach (var link in pending)
                _siteIconCache.TryAdd(HistoryStore.SiteIconKey(link.Url)!, null);
        }
        foreach (var link in links)
            if (HistoryStore.SiteIconKey(link.Url) is { } site && _siteIconCache.TryGetValue(site, out var icon)) link.Icon = icon;
    }

    private static ImageSource? DecodeDataUri(string dataUri)
    {
        try
        {
            var comma = dataUri.IndexOf(',');
            if (comma < 0) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(dataUri[(comma + 1)..]));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    private void SidebarFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshLinksPanel();

    // O teclado das listas passa pelo filtro (as linhas não recebem o foco, como na lista de abas e no Radar):
    // ↑/↓ e Page Up/Down escolhem a linha, Enter abre-a (sem nenhuma escolhida, a primeira), Ctrl+Enter abre-a numa
    // aba nova por trás, Shift+Delete tira-a da lista e Shift+F10 ou a tecla de menu dão o menu da linha.
    private void SidebarFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var rows = SidebarRows;
        switch (key)
        {
            case Key.Escape:
                // Esc limpa o filtro; sem filtro, fecha a barra (como nas notas).
                if (SidebarFilter.Text.Length > 0) SidebarFilter.Clear();
                else CloseSidebar();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                SetSidebarCursor(SidebarLists.MoveCursor(rows, _sidebarCursor, key == Key.Down ? 1 : -1));
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp:
                var target = _sidebarCursor;
                for (var i = 0; i < 8; i++) target = SidebarLists.MoveCursor(rows, target, key == Key.PageDown ? 1 : -1);
                SetSidebarCursor(target);
                e.Handled = true;
                break;
            case Key.Enter when (SidebarCurrentLink ?? rows.OfType<SidebarLink>().FirstOrDefault()) is { } link:
                if (ctrl) Tabs.NewTab(link.Url, activate: false);
                else OpenSidebarLink(link);
                e.Handled = true;
                break;
            case Key.Delete when shift && SidebarCurrentLink is { } link:
                RemoveSidebarLink(link);
                e.Handled = true;
                break;
            case Key.Apps or Key.F10 when (key == Key.Apps || shift) && SidebarCurrentLink is { } link:
                var anchor = SidebarLinkList.ItemContainerGenerator.ContainerFromIndex(_sidebarCursor) as FrameworkElement;
                ShowSidebarLinkMenu(link, anchor ?? SidebarFilter);
                e.Handled = true;
                break;
        }
    }

    // Um botão por painel na coluna de fora — os embutidos, um traço, os sites fixados e, no fim, «fixar a página
    // aberta»; refeita quando a lista de sites muda.
    private void BuildSidebarRail()
    {
        SidebarRail.Children.Clear();
        _railButtons.Clear();
        var style = (Style)FindResource("SidebarRailButton");
        foreach (var panel in SidebarPanels.Available(IsPrivate))
        {
            var button = new Button { Style = style, Content = panel.Glyph, ToolTip = panel.Title, Tag = _sidebarPanel?.Key == panel.Key ? "on" : null };
            var key = panel.Key;
            button.Click += (_, _) => ToggleSidebar(key);
            SidebarRail.Children.Add(button);
            _railButtons[key] = button;
        }
        if (IsPrivate) return;
        if (_webPanels.Count > 0)
        {
            var divider = new Border { Height = 1, Margin = new Thickness(12, 6, 12, 6) };
            divider.SetResourceReference(Border.BackgroundProperty, "SubtleBorderBrush");
            SidebarRail.Children.Add(divider);
        }
        foreach (var site in _webPanels)
        {
            var panel = WebPanels.ToPanel(site);
            var button = new Button
            {
                Style = style, Content = WebPanelRailContent(site), ToolTip = $"{panel.Title} — site fixado (botão direito para opções)",
                Tag = _sidebarPanel?.Key == panel.Key ? "on" : null,
            };
            var key = panel.Key;
            button.Click += (_, _) => ToggleSidebar(key);
            button.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowWebPanelRailMenu(site, button);
            };
            SidebarRail.Children.Add(button);
            _railButtons[key] = button;
        }
        var pin = new Button { Style = style, Content = WebPanels.Glyph, ToolTip = "Fixar a página aberta na barra lateral", Margin = new Thickness(4, 8, 4, 2), Opacity = 0.75 };
        pin.Click += (_, _) => PinCurrentSite();
        SidebarRail.Children.Add(pin);
    }

    // Ícone guardado de um site (o mesmo das listas), ou null.
    private ImageSource? StoredSiteIcon(string url)
    {
        if (IsPrivate || HistoryStore.SiteIconKey(url) is not { } site) return null;
        if (!_siteIconCache.TryGetValue(site, out var icon))
        {
            icon = AppData.SiteIcons([url]).TryGetValue(site, out var dataUri) ? DecodeDataUri(dataUri) : null;
            _siteIconCache[site] = icon;
        }
        return icon;
    }

    // ---------- Pega de redimensionar ----------

    private void SidebarGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _sidebarWidth = SidebarPanels.Resize(_sidebarWidth, e.HorizontalChange, SidebarSideNow, ActualWidth);
        PlaceSidebar();
    }

    private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!IsPrivate) Settings.Set("sidebarWidth", ((int)Math.Round(_sidebarWidth)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void SidebarClose_Click(object sender, RoutedEventArgs e) => CloseSidebar();

    // Clique abre na aba atual; clique do meio ou Ctrl+clique numa aba nova (como nas barras laterais do Firefox).
    private void SidebarLink_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarLink link) return;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            Tabs.NewTab(link.Url, activate: false);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            OpenSidebarLink(link);
            e.Handled = true;
        }
    }

    private void OpenSidebarLink(SidebarLink link)
    {
        LoadUrl(link.Url);
        if (SidebarShows(SidebarPanels.Reading) && !IsPrivate && !link.Muted)
        {
            AppData.MarkRead(link.Url, true);
            RefreshLinksPanel();
        }
    }

    private void SidebarLink_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SidebarLink link } row) return;
        e.Handled = true;
        ShowSidebarLinkMenu(link, row);
    }

    private void ShowSidebarLinkMenu(SidebarLink link, FrameworkElement anchor)
    {
        var menu = new DarkMenu()
            .Item("", "Abrir numa nova aba", () => Tabs.NewTab(link.Url), "Ctrl+Enter")
            .Item("", "Abrir ao lado (ecrã dividido)", () => OpenSideBySide(Tabs.NewTab(link.Url, activate: false)))
            .Item("", "Copiar endereço", () => CopyText(Shields.UrlCleaner.Clean(link.Url), "Endereço copiado"));
        if (SidebarShows(SidebarPanels.Bookmarks))
            menu.Separator().Item("\uE74D", "Remover dos favoritos", () => RemoveSidebarLink(link), "Shift+Del");
        if (SidebarShows(SidebarPanels.History))
            menu.Separator().Item("\uE74D", "Apagar do histórico deste dia", () => RemoveSidebarLink(link), "Shift+Del");
        if (SidebarShows(SidebarPanels.Reading))
            menu.Separator()
                .Item(link.Muted ? "" : "", link.Muted ? "Marcar como por ler" : "Marcar como lido", () =>
                {
                    AppData.MarkRead(link.Url, !link.Muted);
                    RefreshLinksPanel();
                }, enabled: !IsPrivate)
                .Item("", "Remover da lista", () => RemoveSidebarLink(link), "Shift+Del", enabled: !IsPrivate);
        menu.Open(anchor);
    }

    // Tirar a linha da lista do painel à vista. No histórico a linha junta as visitas à página nesse dia (as
    // repetições ficam escondidas), por isso apagam-se todas as desse dia — senão a linha ficava, só com a hora da
    // visita anterior.
    private void RemoveSidebarLink(SidebarLink link)
    {
        _keepSidebarCursor = _sidebarCursor >= 0;
        if (SidebarShows(SidebarPanels.Bookmarks))
        {
            // A linha inteira fica guardada: o «Anular» repõe o favorito com a data de criação (no mesmo lugar da lista).
            var saved = AppData.TakeBookmark(link.Url);
            UpdateStar();
            RefreshLinksPanel();
            if (saved is null) return;
            ShowUndoToast("Removido dos favoritos", $"Remover {Eteris.Browser.Tabs.ToastPolicy.Quote(link.Title)} dos favoritos", () =>
            {
                var restored = AppData.RestoreBookmark(saved);
                UpdateStar();
                RefreshLinksPanel();
                return restored;
            }, [link.Url]);
        }
        else if (SidebarShows(SidebarPanels.History))
        {
            var (from, to) = SidebarLists.DayRangeUtc(link.VisitedUtc);
            AppData.TryWrite(h => h.DeleteVisits(link.Url, from, to));
            RefreshLinksPanel();
            ShowToast("Apagado do histórico deste dia");
        }
        else if (SidebarShows(SidebarPanels.Reading) && !IsPrivate)
        {
            // Com a data e o estado lido/por ler: o «Anular» não o devolve ao topo como novo.
            var item = AppData.TakeFromReadingList(link.Url);
            RefreshLinksPanel();
            if (item is null) return;
            ShowUndoToast("Removido de «Para ler»", $"Remover {Eteris.Browser.Tabs.ToastPolicy.Quote(link.Title)} de «Para ler»", () =>
            {
                var restored = AppData.RestoreReadingItem(item);
                RefreshLinksPanel();
                return restored;
            }, [link.Url]);
        }
        _keepSidebarCursor = false;
    }
}
