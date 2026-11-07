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
    private SidebarSide _sidebarSide;
    private readonly Dictionary<string, Button> _railButtons = new(StringComparer.Ordinal);

    private bool SidebarOpen => _sidebarOpen;

    private bool SidebarShows(string key) => _sidebarOpen && _sidebarPanel?.Key == key;

    // Os painéis desta janela: os embutidos (menos os que não cabem na janela privada).
    private IReadOnlyList<SidebarPanel> SidebarAvailable => SidebarPanels.Available(IsPrivate);

    private void WatchSidebar()
    {
        var settings = Settings.Current;
        _sidebarWidth = settings.SidebarWidth;
        _sidebarSide = SidebarPanels.ParseSide(settings.SidebarSide);
        BuildSidebarRail();
        // Aberta como ficou da última vez (nunca na janela privada: começa fechada, como o resto do que é lembrado).
        if (settings.Sidebar && !IsPrivate && SidebarPanels.Resolve(settings.SidebarPanel, SidebarAvailable) is { } panel)
        {
            _sidebarOpen = true;
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
        if (!IsPrivate) Settings.Set("sidebar", "false");
        PlaceSidebar();
        ReturnFocusToPage();
    }

    // Único sítio que põe a coluna no seu lugar: lado, largura, pega e separadores, e as margens da página.
    // Chamado por ApplyChromeLayout (modo foco e ecrã inteiro escondem-na) e ao arrastar a pega.
    private void PlaceSidebar()
    {
        var visible = _sidebarOpen && _chrome.Mode != Theme.ChromeMode.Hidden;
        var left = _sidebarSide == SidebarSide.Left;
        var sideTabs = SideBar.Visibility == Visibility.Visible ? SideBar.Width : 0;
        var margins = SidebarPanels.PageMargins(sideTabs, visible, _sidebarSide, _sidebarWidth);

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
        // As barras que flutuam sobre a página (procurar, pedidos de permissão) seguem a moldura da página.
        FindBar.Margin = new Thickness(0, 12, 24 + margins.Right, 0);
        PermissionBar.Margin = new Thickness(20 + margins.Left, 10, 0, 0);
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
        SidebarReading.Visibility = panel.Key == SidebarPanels.Reading ? Visibility.Visible : Visibility.Collapsed;
        SidebarLinks.Visibility = panel.Key is SidebarPanels.Bookmarks or SidebarPanels.History ? Visibility.Visible : Visibility.Collapsed;

        switch (panel.Key)
        {
            case SidebarPanels.Notes:
                LoadNotes();
                if (focus) NotesText.Focus();
                break;
            case SidebarPanels.Reading:
                RefreshReadingPanel();
                break;
            case SidebarPanels.Bookmarks:
            case SidebarPanels.History:
                SidebarFilterHint.Text = panel.Key == SidebarPanels.Bookmarks ? "Filtrar favoritos" : "Filtrar o histórico";
                if (SidebarFilter.Text.Length > 0) SidebarFilter.Clear();
                else RefreshLinksPanel();
                if (focus) SidebarFilter.Focus();
                break;
        }
    }

    // ---------- Favoritos e Histórico ----------

    private void RefreshLinksPanel()
    {
        if (_sidebarPanel is not { } panel) return;
        var query = SidebarFilter.Text.Trim();
        SidebarFilterHint.Visibility = SidebarFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IReadOnlyList<object> rows = panel.Key switch
        {
            SidebarPanels.Bookmarks => SidebarLists.Bookmarks(AppData.Bookmarks(b => b.All(500)), query),
            SidebarPanels.History => SidebarLists.History(AppData.TryRead(h => h.Visits(400)), DateTime.Now, query),
            _ => [],
        };
        ApplySiteIcons(rows.OfType<SidebarLink>());
        SidebarLinkList.ItemsSource = rows;
        SidebarLinksEmpty.Text = query.Length > 0 ? $"Nada encontrado para «{query}»."
            : panel.Key == SidebarPanels.Bookmarks ? "Ainda não há favoritos. A estrela na barra de endereço (Ctrl+D) guarda a página aberta."
            : "O histórico está vazio.";
        SidebarLinksEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private void SidebarFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                // Esc limpa o filtro; sem filtro, fecha a barra (como nas notas).
                if (SidebarFilter.Text.Length > 0) SidebarFilter.Clear();
                else CloseSidebar();
                e.Handled = true;
                break;
            case Key.Enter when SidebarLinkList.ItemsSource is IEnumerable<object> rows && rows.OfType<SidebarLink>().FirstOrDefault() is { } first:
                OpenSidebarLink(first);
                e.Handled = true;
                break;
        }
    }

    // Um botão por painel na coluna de fora; refeita quando a lista muda (sites fixados).
    private void BuildSidebarRail()
    {
        SidebarRail.Children.Clear();
        _railButtons.Clear();
        var style = (Style)FindResource("SidebarRailButton");
        foreach (var panel in SidebarAvailable)
        {
            var button = new Button { Style = style, Content = panel.Glyph, ToolTip = panel.Title, Tag = _sidebarPanel?.Key == panel.Key ? "on" : null };
            var key = panel.Key;
            button.Click += (_, _) => ToggleSidebar(key);
            SidebarRail.Children.Add(button);
            _railButtons[key] = button;
        }
    }

    // ---------- Pega de redimensionar ----------

    private void SidebarGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _sidebarWidth = SidebarPanels.Resize(_sidebarWidth, e.HorizontalChange, _sidebarSide, ActualWidth);
        PlaceSidebar();
    }

    private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!IsPrivate) Settings.Set("sidebarWidth", ((int)Math.Round(_sidebarWidth)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void SidebarClose_Click(object sender, RoutedEventArgs e) => CloseSidebar();

    // ---------- Para ler ----------

    private void RefreshReadingPanel()
    {
        var items = SidebarLists.Reading(AppData.ReadingList(), DateTime.UtcNow);
        ReadingList.ItemsSource = items;
        ReadingEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
            RefreshReadingPanel();
        }
    }

    private void SidebarLink_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SidebarLink link } row) return;
        e.Handled = true;
        var menu = new DarkMenu()
            .Item("", "Abrir numa nova aba", () => Tabs.NewTab(link.Url))
            .Item("", "Abrir ao lado (ecrã dividido)", () => OpenSideBySide(Tabs.NewTab(link.Url, activate: false)))
            .Item("", "Copiar endereço", () => CopyText(Shields.UrlCleaner.Clean(link.Url), "Endereço copiado"));
        if (SidebarShows(SidebarPanels.Bookmarks))
            menu.Separator()
                .Item("\uE74D", "Remover dos favoritos", () =>
                {
                    AppData.RemoveBookmark(link.Url);
                    UpdateStar();
                    RefreshLinksPanel();
                    ShowToast("Removido dos favoritos");
                });
        if (SidebarShows(SidebarPanels.History))
            menu.Separator()
                .Item("\uE74D", "Apagar esta visita do histórico", () =>
                {
                    AppData.TryWrite(h => h.DeleteVisit(link.Id));
                    RefreshLinksPanel();
                });
        if (SidebarShows(SidebarPanels.Reading))
            menu.Separator()
                .Item(link.Muted ? "" : "", link.Muted ? "Marcar como por ler" : "Marcar como lido", () =>
                {
                    AppData.MarkRead(link.Url, !link.Muted);
                    RefreshReadingPanel();
                }, enabled: !IsPrivate)
                .Item("", "Remover da lista", () =>
                {
                    AppData.RemoveFromReadingList(link.Url);
                    RefreshReadingPanel();
                    ShowToast("Removido de «Para ler»");
                }, enabled: !IsPrivate);
        menu.Open(row);
    }
}
