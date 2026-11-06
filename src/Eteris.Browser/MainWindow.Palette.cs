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
        PaletteInput.Focus();
    }

    private void ClosePalette()
    {
        if (!PaletteOpen) return;
        PaletteOverlay.Visibility = Visibility.Collapsed;
        BrowserHost.Effect = null;
        if (ActiveBrowser is { } browser) Keyboard.Focus(browser);
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
        if (s_closedWindows.Latest is { } closedWindow)
            yield return new("\uE8A7", $"Reabrir janela fechada ({closedWindow.Addresses.Count} abas)",
                closedWindow.Titles.FirstOrDefault() ?? string.Empty, null, ReopenClosedWindow);
        yield return new("", "Reabrir aba fechada", Tabs.RecentlyClosed.FirstOrDefault() is { } last ? $"«{last.Title}»" : "Nenhuma aba fechada nesta sessão", "Ctrl+Shift+T", () => Tabs.ReopenClosed());
        foreach (var closed in Tabs.RecentlyClosed.Skip(1).Take(5))
        {
            var entry = closed;
            yield return new("", $"Reabrir «{entry.Title}»", entry.Address, null, () => Tabs.ReopenClosed(entry));
        }
        yield return new("", "Esquecer os últimos 15 minutos", "Apaga o histórico desse período e as abas fechadas recentes", null, () => Forget(15));
        yield return new("", "Esquecer a última hora", "Apaga o histórico da última hora e as abas fechadas recentes", null, () => Forget(60));
        yield return new("", "Adormecer abas em segundo plano", "Liberta a memória das abas que não está a usar", null,
            SleepAllBackgroundTabs);
        yield return new("", "Nova janela", "Abrir outra janela do Éteris", "Ctrl+N", () => OpenWindow(isPrivate: false));
        yield return new("", "Nova janela privada", "Nada fica no histórico, nos cookies nem na cache", "Ctrl+Shift+N",
            () => OpenWindow(isPrivate: true));
        if (tab is not null)
        {
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
            yield return new("", "Copiar endereço", address, null, () => Clipboard.SetText(address));
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
            yield return new("", "Esconder um elemento desta página", "Clique num anúncio, banner ou caixa para o fazer desaparecer neste site", null, PickElementToHide);
            if (AppData.HiddenElements(pickHost).Count is > 0 and var hiddenCount)
                yield return new("", "Repor elementos escondidos", $"{hiddenCount} escondido(s) em {pickHost}", null, ResetHiddenElements);
        }
        yield return new("", "Seguir um link pelo teclado", "Letras sobre cada link; escreva-as para o abrir", "Ctrl+Shift+L", ShowLinkHints);
        if (Tabs.Active?.IsReaderable == true)
            yield return new("", "Modo de leitura", "Só o artigo, sem anúncios nem distrações", "F9", ToggleReader);
        if (IsSplit)
            yield return new("", "Fechar ecrã dividido", "Voltar a uma página de cada vez", null, CloseSplit);
        else if (SplitCandidate() is { } partner)
            yield return new("", "Dividir ecrã", $"Mostrar «{partner.Title}» ao lado", null, () => OpenSideBySide(partner));
        var light = Theme.ThemeManager.Current == Theme.AppTheme.Light;
        yield return new(light ? "" : "", light ? "Mudar para tema escuro" : "Mudar para tema claro",
            "Também pode seguir o Windows em Definições › Aspeto", null, () => Data.Settings.Set("theme", light ? "dark" : "light"));
        yield return new("", VerticalTabs ? "Abas no topo" : "Abas verticais",
            VerticalTabs ? "Voltar à faixa de abas clássica" : "Lista de abas na lateral, ideal para muitas abas", null, ToggleVerticalTabs);
        yield return new("", "Atalhos de teclado", "Todos os atalhos do Éteris numa só vista", "F1", ToggleShortcuts);
        yield return new("\uE789", "Novidades desta versão", "O que mudou no Éteris", null, () => OpenInternal(WhatsNewPage.Url));
        yield return new("", "Definições", "Motor de pesquisa, arranque, abas e privacidade", "Ctrl+,", OpenSettings);
        yield return new("\uE7BC", "Lista de leitura", "Páginas guardadas para ler mais tarde", null, () => OpenInternal(BookmarksPage.Url + "#ler"));
        yield return new("", "Favoritos", "Ver, pesquisar, renomear e remover favoritos", "Ctrl+Shift+O", OpenBookmarks);
        yield return new("", "Histórico", "Ver, pesquisar e apagar páginas visitadas", "Ctrl+H", OpenHistory);
        yield return new("", "Aumentar zoom", $"Atual: {zoom}%", "Ctrl +", () => Zoom(1));
        yield return new("", "Reduzir zoom", $"Atual: {zoom}%", "Ctrl −", () => Zoom(-1));
        yield return new("", "Repor zoom", "Voltar a 100%", "Ctrl+0", () => Zoom(0));
        yield return new("", _focusMode ? "Sair do modo foco" : "Modo foco",
            "Esconde abas e barras para ler sem distrações", "F11", ToggleFocusMode);
    }

    private void RefreshPalette()
    {
        var query = PaletteInput.Text.Trim();
        PalettePlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var tabs = Tabs.Tabs
            .Where(t => !ReferenceEquals(t, Tabs.Active))
            .Select(t => new PaletteCommand("", t.Title, "Mudar para esta aba · " + (WebHost(t.Address) ?? "Éteris"), null,
                () => Tabs.Active = t));

        var items = CommandSearch.RankTitled(query, Actions().Concat(tabs), c => c.Title, c => c.Subtitle);

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

    private void Menu_Click(object sender, RoutedEventArgs e) => OpenPalette();

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
        _focusMode = !_focusMode;
        ApplyChromeLayout();
        CloseSuggestions();
        ShieldPopup.IsOpen = false;

        if (!_focusMode) return;
        var toast = new DoubleAnimationUsingKeyFrames();
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        toast.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        FocusToast.BeginAnimation(OpacityProperty, toast);
    }

    // ---------- Íris adaptativa: a interface ganha o tom do site ----------

    private void DetectSiteTint(BrowserTab tab, CefSharp.Wpf.ChromiumWebBrowser browser, string url)
    {
        if (NewTabPage.IsNewTab(url))
        {
            tab.Tint = null;
            return;
        }

        browser.EvaluateScriptAsync(ThemeColorScript).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result.Success && task.Result.Result is string css)
                Dispatcher.InvokeAsync(() => tab.Tint = SiteTint.Parse(css) is { } color ? SiteTint.ForChrome(color) : null);
        });
    }

    private void ApplyTint()
    {
        // As cores dos sites estão afinadas para o tema escuro; no claro a superfície fica neutra.
        var tint = UsesLight ? LightChrome : Tabs.Active?.Tint ?? SiteTint.Base;
        // Com Mica, a superfície das barras fica translúcida para o material do sistema transparecer.
        var target = _mica ? Color.FromArgb(ChromeAlpha, tint.R, tint.G, tint.B) : tint;
        var brush = (SolidColorBrush)Resources["ChromeSurfaceBrush"];
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }
}
