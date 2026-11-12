using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser;

public partial class MainWindow
{
    private bool _omniboxEdited;
    private bool _settingOmnibox;
    private bool _suppressCompletion;
    private string _typed = string.Empty;

    private static SearchEngine Engine => SearchEngine.Preferred;

    // Nunca sobrescreve o que o utilizador está a escrever; fora disso a omnibox espelha sempre a aba ativa — também
    // com o foco nela sem nada escrito (Ctrl+T e logo Ctrl+1: mostra o endereço da aba para onde se foi, selecionado).
    private void SyncOmnibox()
    {
        if (!_omniboxEdited)
        {
            var shown = Tabs.Active is { } current ? AddressBar.Text(current.Address, current.PendingAddress) : string.Empty;
            var text = NewTabPage.IsNewTab(shown) ? string.Empty : shown;
            if (!Omnibox.IsKeyboardFocusWithin) SetOmniboxText(text);
            else if (Omnibox.Text != text)
            {
                SetOmniboxText(text);
                Omnibox.SelectAll();
            }
        }
        UpdateOmniboxChrome();
    }

    private void SetOmniboxText(string text)
    {
        _settingOmnibox = true;
        Omnibox.Text = text;
        _settingOmnibox = false;
    }

    private void UpdateOmniboxChrome()
    {
        var focused = Omnibox.IsKeyboardFocusWithin;
        var empty = Omnibox.Text.Length == 0;
        var showDisplay = !focused && !empty;

        // Em modo compacto a barra é estreita: o convite fica curto.
        OmniboxPlaceholder.Text = Compact ? "Pesquisar ou escrever um endereço" : $"Pesquisar com {Engine.Name} ou escrever um endereço";
        OmniboxPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        OmniboxDisplay.Visibility = showDisplay ? Visibility.Visible : Visibility.Collapsed;
        Omnibox.Foreground = showDisplay ? Brushes.Transparent : (Brush)FindResource("TextBrush");
        if (showDisplay)
        {
            // O domínio registável nunca sai da vista: se o nome não cabe, cortam-se os subdomínios pela esquerda; o caminho
            // é cortado no fim pelo TextTrimming. Com a barra ainda sem largura (0) não se ajusta nada.
            var parts = UrlDisplay.Parts(Omnibox.Text);
            if (OmniboxDisplay.ActualWidth > 0)
            {
                // Reserva a reticência com que o TextTrimming fecha o caminho cortado, para ela não comer o fim do domínio.
                var reserve = 1 + (parts.Rest.Length > 0 ? MeasureOmniboxText("…") : 0);
                parts = UrlDisplay.Fit(parts, OmniboxDisplay.ActualWidth - reserve, MeasureOmniboxText);
            }
            DisplaySub.Text = parts.Subdomains;
            DisplayHost.Text = parts.Domain;
            DisplayRest.Text = parts.Rest;
        }

        // O cadeado só segue o endereço confirmado pelo motor: pedido por carregar, texto por enviar e página de erro não o têm.
        var active = Tabs.Active;
        var showsError = active?.ShowsErrorPage == true;
        var kind = AddressBar.Icon(active?.Address, active?.PendingAddress, showsError, _omniboxEdited, focused, empty);
        SiteIcon.Text = kind switch
        {
            null => "",
            ConnectionKind.Secure => "",
            ConnectionKind.Insecure => "",
            ConnectionKind.Internal => "",
            _ => "",
        };
        var site = SiteInfo.Describe(active?.Address ?? Omnibox.Text, failed: showsError);
        SiteInfoButton.ToolTip = focused || empty ? null
            : active?.PendingAddress is not null || _omniboxEdited ? "Ainda não carregou — ver detalhes do site"
            : $"{site.Title} — ver detalhes do site";
        SiteInfoButton.IsHitTestVisible = !focused && !empty;
    }

    // A largura do texto na fonte e no tamanho da barra (o mesmo tipo de letra para subdomínios, domínio e caminho).
    private double MeasureOmniboxText(string text)
        => new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection,
                new Typeface(OmniboxDisplay.FontFamily, OmniboxDisplay.FontStyle, OmniboxDisplay.FontWeight, OmniboxDisplay.FontStretch),
                OmniboxDisplay.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    // A janela mudou de largura: o que cabe na barra muda com ela.
    private void OmniboxDisplay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateOmniboxChrome();
    }

    private void Go(string input, bool newTab = false)
    {
        if (OmniboxClassifier.Classify(input, Engine) is { } result) LoadUrl(result.Url, newTab);
    }

    private async void LoadUrl(string url, bool newTab = false)
    {
        CloseSuggestions();
        _omniboxEdited = false;
        if (newTab)
        {
            Tabs.NewTab(url);
            return;
        }

        if (Tabs.Active is { } videoTab && LoadInVideoView(videoTab, url))
        {
            SetOmniboxText(url);
            UpdateOmniboxChrome();
            return;
        }
        // Site que abre no motor de vídeo (lembrado ou da lista de sites só H.264): vai logo para lá, sem carregar a
        // página também no motor principal; a página atual fica à vista até o motor de vídeo a ter carregado.
        if (Tabs.Active is { UsesVideoEngine: false } known && IsVideoSite(url))
        {
            SetOmniboxText(url);
            UpdateOmniboxChrome();
            SwitchToVideoEngine(known, url);
            return;
        }
        if (ActiveBrowser is not { } browser || Tabs.Active is not { } tab) return;
        // Só o motor confirma o endereço (AddressChanged): até lá a aba continua na página anterior.
        tab.PendingAddress = url;
        tab.PendingLoadStarted = false;
        SetOmniboxText(url);
        Keyboard.Focus(browser);
        UpdateOmniboxChrome();
        await ScriptsReadyFor(tab, browser, url);
        if (browser.IsDisposed) return;
        tab.PendingLoadStarted = true;
        browser.Load(url);
    }

    private void FocusOmnibox()
    {
        RevealBarsForKeyboard();
        Omnibox.Focus();
        Omnibox.SelectAll();
    }

    private void RefreshSuggestions()
    {
        var text = Omnibox.Text;
        var history = AppData.TryRead(h => h.Search(text, 8));

        if (!_suppressCompletion && Omnibox.CaretIndex == text.Length
            && Suggestions.InlineCompletion(text, history) is { } completion)
        {
            SetOmniboxText(completion);
            Omnibox.Select(text.Length, completion.Length - text.Length);
        }
        _suppressCompletion = false;

        var bookmarks = AppData.Bookmarks(b => b.Search(text, 3)).Select(b => new HistoryEntry(b.Url, b.Title, 0)).ToList();
        var openTabs = Tabs.Tabs.Where(t => !ReferenceEquals(t, Tabs.Active) && !t.IsNewTab)
            .Select(t => new HistoryEntry(t.Address, t.Title, 0)).ToList();
        var list = Suggestions.Build(Omnibox.Text, Engine, history, bookmarks, openTabs);
        SuggestionList.ItemsSource = list;
        SuggestionList.SelectedIndex = PrimaryIndex(list);
        SuggestionPopup.IsOpen = list.Count > 0 && Omnibox.IsKeyboardFocusWithin;
        RequestRemoteSuggestions(Omnibox.Text, list);
    }

    private CancellationTokenSource? _suggestRequest;

    // Sugestões do motor (opcional): pedido com atraso curto, cancelado a cada tecla, nunca em janelas privadas.
    private async void RequestRemoteSuggestions(string text, IReadOnlyList<Suggestion> local)
    {
        _suggestRequest?.Cancel();
        if (IsPrivate || !Data.Settings.Current.SearchSuggestions || !SearchSuggest.ShouldQuery(text, Engine)) return;
        var request = _suggestRequest = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, request.Token);
            var remote = await SearchSuggest.FetchAsync(Engine, text.Trim(), request.Token);
            if (request.IsCancellationRequested || remote.Count == 0 || Omnibox.Text != text || !SuggestionPopup.IsOpen) return;
            var selected = SuggestionList.SelectedIndex;
            var merged = SearchSuggest.Merge(local, remote, Engine);
            SuggestionList.ItemsSource = merged;
            SuggestionList.SelectedIndex = selected == PrimaryIndex(local) ? PrimaryIndex(merged) : selected;
        }
        catch (OperationCanceledException) { }
    }

    private void CloseSuggestions() => SuggestionPopup.IsOpen = false;

    // A resposta instantânea fica no topo, mas o Enter continua a pesquisar/abrir o que foi escrito.
    private static int PrimaryIndex(IReadOnlyList<Suggestion> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Kind != SuggestionKind.Answer) return i;
        return -1;
    }

    private void OpenSuggestion(Suggestion s, bool newTab = false)
    {
        if (s.Kind == SuggestionKind.Answer) { CopyAnswer(s); return; }
        if (s.Kind == SuggestionKind.Tab && Tabs.Tabs.FirstOrDefault(t => t.Address == s.Url) is { } open)
        {
            CloseSuggestions();
            _omniboxEdited = false;
            Tabs.Active = open;
            return;
        }
        LoadUrl(s.Url, newTab);
    }

    private void CopyAnswer(Suggestion answer)
    {
        try { Clipboard.SetText(answer.Text.TrimStart('=', ' ')); } catch { }
        CloseSuggestions();
        ShowToast($"Copiado: {answer.Text.TrimStart('=', ' ')}");
    }

    private void MoveSuggestion(int delta)
    {
        var count = SuggestionList.Items.Count;
        if (count == 0) return;
        SuggestionList.SelectedIndex = (SuggestionList.SelectedIndex + delta + count) % count;
        if (SuggestionList.SelectedItem is Suggestion s)
        {
            SetOmniboxText(s.Kind switch { SuggestionKind.History => s.Url, SuggestionKind.Answer => _typed, _ => s.Text });
            Omnibox.CaretIndex = Omnibox.Text.Length;
        }
    }

    private void Omnibox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Back or Key.Delete:
                _suppressCompletion = true;
                break;
            case Key.Down when SuggestionPopup.IsOpen:
                MoveSuggestion(1);
                e.Handled = true;
                break;
            case Key.Up when SuggestionPopup.IsOpen:
                MoveSuggestion(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (SuggestionPopup.IsOpen && SuggestionList.SelectedItem is Suggestion s
                    && SuggestionList.SelectedIndex != PrimaryIndex((IReadOnlyList<Suggestion>)SuggestionList.ItemsSource))
                    OpenSuggestion(s, newTab: alt);
                else
                    Go(Omnibox.Text, newTab: alt);
                e.Handled = true;
                break;
            case Key.Escape when SuggestionPopup.IsOpen:
                CloseSuggestions();
                SetOmniboxText(_typed);
                Omnibox.CaretIndex = Omnibox.Text.Length;
                e.Handled = true;
                break;
            case Key.Escape:
                _omniboxEdited = false;
                if (ActiveBrowser is { } browser) Keyboard.Focus(browser);
                SyncOmnibox();
                e.Handled = true;
                break;
        }
    }

    private void Omnibox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingOmnibox) return;
        _omniboxEdited = true;
        _typed = Omnibox.Text;
        RefreshSuggestions();
        UpdateOmniboxChrome();
    }

    private void Omnibox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A barra de endereço com o foco: o campo do cofre da página deixa de contar.
        ForgetVaultFocus();
        // O cartão do cadeado é da página atual: escrever outro endereço (Ctrl+L, F6, clique) fecha-o.
        SiteInfoPopup.IsOpen = false;
        Omnibox.SelectAll();
        UpdateOmniboxChrome();
        UpdateCompactOmnibox();
    }

    private void Omnibox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Omnibox.IsKeyboardFocusWithin) return;
        Omnibox.Focus();
        e.Handled = true;
    }

    private void Omnibox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CloseSuggestions();
        if (Omnibox.Text.Length == 0) _omniboxEdited = false;
        SyncOmnibox();
        UpdateCompactOmnibox();
    }

    private void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Suggestion s) OpenSuggestion(s);
        e.Handled = true;
    }
}
