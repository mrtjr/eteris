using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;
using Eteris.Browser.Vault;

namespace Eteris.Browser;

// O cofre na janela (docs/palavras-passe.md): abrir as páginas do gestor de palavras-passe e do preenchimento
// automático, e o que essas páginas pedem ao processo do navegador (WindowsVaultHost). As páginas recusam-se na janela
// privada, por isso dali abrem numa janela normal.
public partial class MainWindow
{
    internal void OpenVaultPage(string url)
    {
        if (!VaultPages.OpensInNormalWindow(IsPrivate))
        {
            OpenVaultPageHere(url);
            return;
        }
        if (NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is { } normal)
        {
            normal.OpenVaultPageHere(url);
            normal.Activate();
        }
        else OpenWindow(isPrivate: false, url);
    }

    // Reaproveita a aba da mesma página (eteris://autofill/ com outra secção: muda só a secção).
    private void OpenVaultPageHere(string url)
    {
        var page = url.Split('#')[0];
        if (Tabs.Tabs.FirstOrDefault(t => t.Address.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is not { } open)
        {
            Tabs.NewTab(url);
            return;
        }
        Tabs.Active = open;
        if (!string.Equals(open.Address, url, StringComparison.OrdinalIgnoreCase)) LoadUrl(url);
    }

    // Pedidos de reautenticação à espera: a aba que os fez saiu da frente (outra aba, outra janela fechou-a) → desistem.
    private static readonly List<CancellationTokenSource> s_vaultFront = [];

    internal static CancellationToken VaultFrontToken()
    {
        var source = new CancellationTokenSource();
        lock (s_vaultFront)
        {
            s_vaultFront.RemoveAll(s => s.IsCancellationRequested);
            s_vaultFront.Add(source);
        }
        return source.Token;
    }

    private static void CancelVaultRequests()
    {
        List<CancellationTokenSource> all;
        lock (s_vaultFront)
        {
            all = [.. s_vaultFront];
            s_vaultFront.Clear();
        }
        foreach (var source in all) source.Cancel();
    }

    // A janela fechou: os pedidos dela desistem e a janela de graça dela acaba.
    private void ForgetVaultWindow()
    {
        CancelVaultRequests();
        ReauthGate.ForgetWindowIfCreated(new WindowInteropHelper(this).Handle);
    }

    internal void OpenTabFromVault(string url) => Tabs.NewTab(url);

    // ---- O canal com o script do cofre em cada aba (Vault/VaultChannel.cs) ----

    // Lido e escrito na thread do CEF (commit, fim do carregamento) e na da janela (fechar, adormecer).
    private readonly Dictionary<BrowserTab, VaultChannel> _vaultChannels = [];
    private readonly Lock _vaultChannelsGate = new();

    // Thread do CEF: commit ou fim do carregamento do frame de topo. Só páginas http(s); o canal nasce na 1.ª e o cofre
    // não abre (só abre quando um campo pede sugestões).
    private void VaultPageCommitted(BrowserTab tab, ChromiumWebBrowser browser)
    {
        if (browser.IsDisposed || browser.GetBrowser() is not { } cef) return;
        var url = SafeFrame.MainUrl(cef);
        if (VaultChannel.EligibleOrigin(url, url) is null) return;
        VaultChannel channel;
        lock (_vaultChannelsGate)
        {
            if (!_vaultChannels.TryGetValue(tab, out var known) || known.IsDisposed)
                _vaultChannels[tab] = known = new VaultChannel(cef,
                    (context, message) => Dispatcher.InvokeAsync(() => OnVaultMessage(tab, context, message)),
                    () => Dispatcher.InvokeAsync(() => OnVaultNavigated(tab)),
                    // Oferecer guardar e gerar: nunca na janela privada (D-3), nem com o interruptor desligado.
                    () => !IsPrivate && Data.Settings.Current.PasswordOffer);
            channel = known;
        }
        _ = Task.Run(channel.Refresh);
    }

    // A aba fechou, adormeceu ou passou para o motor de vídeo.
    private void ReleaseVaultChannel(BrowserTab tab)
    {
        VaultChannel? channel;
        // O candidato e a oferta de guardar morrem com a aba (só existiam na memória).
        Dispatcher.InvokeAsync(() => ForgetSaveState(tab));
        lock (_vaultChannelsGate)
        {
            if (!_vaultChannels.Remove(tab, out channel)) return;
        }
        channel.Dispose();
    }

    private VaultChannel? VaultChannelOf(BrowserTab tab)
    {
        lock (_vaultChannelsGate) return _vaultChannels.GetValueOrDefault(tab);
    }

    // ---- Sugestões, teclado e preencher palavras-passe (docs/palavras-passe.md, C3) ----

    // O campo com o foco numa página (o último «focus» do script): a aba, o documento e o que o script disse do campo.
    private sealed record VaultFocus(BrowserTab Tab, VaultContext Context, string Field, string Form, string Kind, string? FormKind, VaultRect Rect)
    {
        public IReadOnlyList<LoginEntry> Logins { get; init; } = [];
        public IReadOnlyList<SuggestionRow> Rows { get; init; } = [];
        public override string ToString() => $"VaultFocus {Field}";
    }

    private VaultFocus? _vaultFocus;
    private SuggestionList? _vaultList;
    private DateTime _vaultShownAt;
    private bool _vaultHooked;

    // Lidos na thread do CEF (as teclas da página, SuggestionKeyFromPage); escritos só na thread da janela.
    private volatile bool _vaultListOpen;
    private volatile bool _vaultHasSelection;
    private volatile bool _vaultCanOpen;

    // Uma mensagem validada do script (thread da janela).
    private void OnVaultMessage(BrowserTab tab, VaultContext context, VaultMessage message)
    {
        switch (message.Type)
        {
            case VaultMessageType.Focus:
                OnVaultFieldFocus(tab, context, message);
                break;
            case VaultMessageType.Input when IsVaultFocus(tab, context, message.Field):
                OnVaultFieldInput(message);
                break;
            case VaultMessageType.Blur when IsVaultFocus(tab, context, message.Field):
                ForgetVaultFocus();
                break;
            case VaultMessageType.Scroll when IsVaultFocus(tab, context, message.Field):
                if (message.Rect is { } rect) _vaultFocus = _vaultFocus! with { Rect = rect };
                if (_vaultListOpen) PlaceVaultList();
                break;
            case VaultMessageType.Submit:
                OnVaultSubmit(tab, context, message);
                break;
            case VaultMessageType.FormGone:
                OnVaultFormGone(tab, context, message);
                break;
        }
    }

    // O frame de topo mudou de documento (thread da janela).
    private void OnVaultNavigated(BrowserTab tab)
    {
        if (_vaultFocus?.Tab == tab) ForgetVaultFocus();
        OnSaveNavigation(tab);
    }

    private bool IsVaultFocus(BrowserTab tab, VaultContext context, string? field)
        => _vaultFocus is { } focus && focus.Tab == tab && focus.Context.Id == context.Id && focus.Field == field;

    private void ForgetVaultFocus()
    {
        _vaultFocus = null;
        _vaultCanOpen = false;
        CloseVaultList();
    }

    // Um campo recebeu o foco (clique, Tab, ou um clique num campo que já o tinha). As sugestões só aparecem na aba à
    // vista, num campo visível de utilizador ou de palavra-passe atual, se houver palavras-passe deste site; o cofre só
    // abre aqui (a primeira vez que um campo destes as pede).
    private void OnVaultFieldFocus(BrowserTab tab, VaultContext context, VaultMessage message)
    {
        ForgetVaultFocus();
        if (!message.Visible || message.Field is not { } field || message.Form is not { } form || message.Kind is not { } kind
            || message.Rect is not { } rect || !IsTabOnScreen(tab)) return;
        if (!VaultFillPolicy.OffersLogins(kind, message.FormKind)) return;
        IReadOnlyList<LoginEntry> logins;
        try { logins = VaultService.Profile.LoginsFor(context.Origin); }
        catch (Exception) { return; }
        if (logins.Count == 0) return;
        var rows = VaultFillPolicy.Suggestions(logins, context.Origin, kind == "username" ? message.Prefix : null);
        _vaultFocus = new VaultFocus(tab, context, field, form, kind, message.FormKind, rect) { Logins = logins, Rows = rows };
        HookVaultList();
        _vaultCanOpen = rows.Count > 0;
        if (rows.Count > 0 && Data.Settings.Current.PasswordSuggestions) ShowVaultList(selected: -1);
    }

    // O utilizador escreve: no campo do utilizador, a lista filtra pelo que já escreveu; na palavra-passe, fecha.
    private void OnVaultFieldInput(VaultMessage message)
    {
        if (_vaultFocus is not { } focus) return;
        if (focus.Kind != "username")
        {
            CloseVaultList();
            return;
        }
        var rows = VaultFillPolicy.Suggestions(focus.Logins, focus.Context.Origin, message.Prefix);
        _vaultFocus = focus with { Rows = rows };
        _vaultCanOpen = rows.Count > 0;
        if (rows.Count == 0) CloseVaultList();
        else if (_vaultListOpen) ShowVaultList(selected: -1);
    }

    private void ShowVaultList(int selected)
    {
        if (_vaultFocus is not { Rows.Count: > 0 } focus) return;
        if (_vaultList is null)
        {
            _vaultList = new SuggestionList
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRowSpan(_vaultList, 3);
            Panel.SetZIndex(_vaultList, 90);
            _vaultList.Chosen += index =>
            {
                // Um clique logo a seguir a a lista aparecer não conta (cliques roubados).
                if (!VaultFillPolicy.AcceptsClick(DateTime.UtcNow - _vaultShownAt)) return;
                _vaultList.Select(index);
                AcceptVaultSuggestion();
            };
            _vaultList.ManageRequested += () =>
            {
                CloseVaultList();
                OpenVaultPage("eteris://passwords/");
            };
            AddToOverlay(_vaultList);
        }
        HookVaultList();
        var wasOpen = _vaultListOpen;
        _vaultList.Show(focus.Rows, selected);
        _vaultList.Visibility = Visibility.Visible;
        if (!wasOpen) _vaultShownAt = DateTime.UtcNow;
        _vaultListOpen = true;
        _vaultHasSelection = _vaultList.Selected >= 0;
        PlaceVaultList();
    }

    // A lista sai sempre do campo: por baixo (ou por cima, sem lugar), dentro da página da aba.
    private void PlaceVaultList()
    {
        if (_vaultList is null || _vaultFocus is not { } focus || !_browsers.TryGetValue(focus.Tab, out var browser)
            || browser.ActualWidth <= 0 || PresentationSource.FromVisual(browser) is null)
        {
            CloseVaultList();
            return;
        }
        var origin = browser.TranslatePoint(new Point(0, 0), RootBorder);
        var page = new Rect(origin, new Size(browser.ActualWidth, browser.ActualHeight));
        var zoom = Palette.ZoomSteps.ToFactor(browser.ZoomLevel);
        var width = Math.Min(Math.Max(focus.Rect.Width * zoom, SuggestionGeometry.MinWidth), page.Width);
        _vaultList.Width = width;
        _vaultList.Measure(new Size(width, double.PositiveInfinity));
        if (SuggestionGeometry.Place(page, focus.Rect, zoom, _vaultList.DesiredSize.Height) is not { } place)
        {
            CloseVaultList();
            return;
        }
        _vaultList.Width = place.Width;
        _vaultList.Margin = new Thickness(place.X, place.Y, 0, 0);
    }

    private void CloseVaultList()
    {
        _vaultListOpen = false;
        _vaultHasSelection = false;
        if (_vaultList is { Visibility: Visibility.Visible }) _vaultList.Visibility = Visibility.Collapsed;
    }

    // Uma vez: a lista fecha quando a janela perde o foco, se mexe ou muda de tamanho, ou a aba muda; e um clique nela
    // não ativa a camada (o foco do teclado fica no campo da página, que não recebe um «blur»).
    private void HookVaultList()
    {
        if (_vaultHooked) return;
        _vaultHooked = true;
        Deactivated += (_, _) => CloseVaultList();
        LocationChanged += (_, _) => CloseVaultList();
        SizeChanged += (_, _) => CloseVaultList();
        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabsViewModel.Active)) ForgetVaultFocus();
        };
        if (_overlay is not null && HwndSource.FromHwnd(new WindowInteropHelper(_overlay).EnsureHandle()) is { } source)
            source.AddHook(VaultOverlayHook);
    }

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private nint VaultOverlayHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmMouseActivate || !_vaultListOpen || _vaultList is null || !_vaultList.IsMouseOver) return 0;
        handled = true;
        return MaNoActivate;
    }

    // Thread do CEF (ShortcutFromPage): as teclas da lista, decididas por VaultKeys (puro). null: não é do cofre.
    private bool? SuggestionKeyFromPage(Key key, bool ctrl, bool shift, bool alt)
    {
        var action = VaultKeys.FromPage(key, ctrl, shift, alt, _vaultListOpen, _vaultHasSelection, _vaultCanOpen);
        if (action == VaultKeyAction.None) return null;
        Dispatcher.InvokeAsync(() => HandleVaultKey(action));
        return VaultKeys.Consumes(action);
    }

    private void HandleVaultKey(VaultKeyAction action)
    {
        switch (action)
        {
            case VaultKeyAction.Open when _vaultFocus is { } focus && IsTabOnScreen(focus.Tab):
                ShowVaultList(selected: 0);
                break;
            case VaultKeyAction.Previous or VaultKeyAction.Next when _vaultList is not null && _vaultListOpen:
                _vaultList.Move(action == VaultKeyAction.Next ? 1 : -1);
                _vaultHasSelection = _vaultList.Selected >= 0;
                break;
            case VaultKeyAction.Accept:
                AcceptVaultSuggestion();
                break;
            case VaultKeyAction.Close or VaultKeyAction.CloseAndPass:
                CloseVaultList();
                break;
        }
    }

    private void AcceptVaultSuggestion()
    {
        if (_vaultFocus is not { } focus || _vaultList?.SelectedRow is not { } row) return;
        CloseVaultList();
        _ = FillVaultLoginAsync(focus, row);
    }

    // Preencher (desenho, secção 3.6): a aba à vista na janela ativa, o mesmo documento e uma palavra-passe deste site;
    // o Windows Hello se o interruptor pedir (D-5); a palavra-passe decifra-se agora e atravessa o DevTools uma vez, só
    // para o contexto do documento que pediu.
    private async Task FillVaultLoginAsync(VaultFocus focus, SuggestionRow row)
    {
        if (VaultChannelOf(focus.Tab) is not { } channel || focus.Logins.FirstOrDefault(l => l.Id == row.Id) is not { } entry) return;
        bool Allowed() => VaultFillPolicy.CanFill(new VaultFillPolicy.FillCheck(IsTabOnScreen(focus.Tab), IsActive || _overlay?.IsActive == true,
            channel.IsCurrent(focus.Context), entry.Origin, focus.Context.Origin)) == FillVerdict.Allowed;
        if (!Allowed()) return;
        if (VaultFillPolicy.NeedsReauth(Data.Settings.Current.PasswordAskHello)
            && !await ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, ReauthPurpose.FillPassword, VaultFrontToken()))
            return;
        if (!Allowed()) return;
        LoginItem? login;
        try { login = VaultService.Profile.GetLogin(entry.Id); }
        catch (Exception) { return; }
        if (login is null) return;
        var values = new Dictionary<string, string> { ["username"] = login.Username, ["password"] = login.Password };
        var result = await channel.CallAsync(focus.Context, "fill", new { field = focus.Field, form = focus.Form, values });
        values.Clear();
        if (!result.Success || result.Result is not double filled || filled < 1)
            ShowToast("Não foi possível preencher este formulário.");
    }

    // ---- Guardar, atualizar e «Nunca neste site» (docs/palavras-passe.md, C4) ----

    // O último envio de cada aba, à espera de saber se correu bem (só na memória; SavePolicy).
    private readonly Dictionary<BrowserTab, SaveCandidate> _vaultCandidates = [];
    // A oferta de cada aba (a bolha, ou o ícone de chave depois de «Agora não») e quando nasceu.
    private readonly Dictionary<BrowserTab, (SaveOffer Offer, DateTime At)> _vaultOffers = [];
    private SaveBubble? _saveBubble;
    private bool _saveHooked;

    private void OnVaultSubmit(BrowserTab tab, VaultContext context, VaultMessage message)
    {
        if (SavePolicy.Candidate(message, context, DateTimeOffset.UtcNow, Data.Settings.Current.PasswordOffer, IsPrivate) is { } candidate)
            _vaultCandidates[tab] = candidate;
    }

    // O formulário enviado desapareceu sem o início de sessão voltar (SPA): correu bem.
    private void OnVaultFormGone(BrowserTab tab, VaultContext context, VaultMessage message)
    {
        if (message.Form is not { } form || !_vaultCandidates.TryGetValue(tab, out var candidate)) return;
        var outcome = SavePolicy.AfterFormGone(candidate, context.Id, form, DateTimeOffset.UtcNow);
        if (outcome == SubmitOutcome.Pending) return;
        _vaultCandidates.Remove(tab);
        if (outcome == SubmitOutcome.Success) OfferSave(tab, candidate);
    }

    // O frame principal mudou de documento: a oferta antiga sai (passados os redirecionamentos) e um envio à espera
    // fica a saber como correu — a página nova tem outra vez o início de sessão da mesma origem? Então foi um erro.
    private void OnSaveNavigation(BrowserTab tab)
    {
        if (_vaultOffers.TryGetValue(tab, out var offer) && !SavePolicy.KeepsOfferAcrossNavigation(DateTime.UtcNow - offer.At))
            DropSaveOffer(tab);
        if (!_vaultCandidates.Remove(tab, out var candidate) || SavePolicy.Expired(candidate, DateTimeOffset.UtcNow)) return;
        _ = JudgeAfterNavigationAsync(tab, candidate, DateTimeOffset.UtcNow);
    }

    private async Task JudgeAfterNavigationAsync(BrowserTab tab, SaveCandidate candidate, DateTimeOffset navigatedAt)
    {
        string? origin = null;
        var loginAgain = false;
        // Até 8 s para o documento novo estar pronto (se não for http(s), não há canal: correu bem).
        for (var i = 0; i < 32; i++)
        {
            await Task.Delay(250);
            if (VaultChannelOf(tab) is not { } channel) break;
            if (channel.MainContext() is not { } main) continue;
            var answer = await channel.CallAsync(main, "probe", null);
            if (!answer.Success || answer.Result is not string state || state == "loading") continue;
            origin = main.Origin;
            loginAgain = state == "login";
            break;
        }
        if (!Tabs.Tabs.Contains(tab)) return;
        if (SavePolicy.AfterNavigation(candidate, origin, loginAgain, navigatedAt) == SubmitOutcome.Success) OfferSave(tab, candidate);
    }

    private void OfferSave(BrowserTab tab, SaveCandidate candidate)
    {
        if (IsPrivate || !Data.Settings.Current.PasswordOffer) return;
        SaveOffer offer;
        try
        {
            var vault = VaultService.Profile;
            var never = vault.IsNeverSave(candidate.Origin);
            var stored = never ? [] : vault.LoginsFor(candidate.Origin).Where(l => l.Match == OriginMatch.Exact)
                .Select(l => vault.GetLogin(l.Id)).OfType<LoginItem>()
                .Select(l => new StoredLogin(l.Id, l.Username, l.Password, l.Generated)).ToList();
            offer = SavePolicy.Decide(candidate, never, stored);
        }
        catch (Exception) { return; }
        switch (offer.Kind)
        {
            case SaveOfferKind.None:
                return;
            case SaveOfferKind.ConfirmGenerated:
                ConfirmGeneratedPassword(offer);
                return;
        }
        _vaultOffers[tab] = (offer, DateTime.UtcNow);
        HookSaveBubble();
        if (ReferenceEquals(tab, Tabs.Active)) ShowSaveBubble(tab);
        UpdatePasswordKey();
    }

    private void ShowSaveBubble(BrowserTab tab)
    {
        if (!_vaultOffers.TryGetValue(tab, out var entry)) return;
        if (_saveBubble is null)
        {
            _saveBubble = new SaveBubble
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRowSpan(_saveBubble, 3);
            Panel.SetZIndex(_saveBubble, 95);
            _saveBubble.SaveRequested += SaveOfferedPassword;
            _saveBubble.NotNowRequested += CloseSaveBubble;
            _saveBubble.NeverRequested += NeverSaveHere;
            AddToOverlay(_saveBubble);
        }
        _saveBubble.Show(entry.Offer);
        PlaceSaveBubble();
        _saveBubble.Visibility = Visibility.Visible;
    }

    // Por baixo do cadeado, com o bico a apontar para ele: a bolha sai da barra de endereço e passa a linha entre as
    // barras e a página (com as barras escondidas, no topo da página).
    private void PlaceSaveBubble()
    {
        if (_saveBubble is null) return;
        var anchor = SiteInfoButton.IsVisible && SiteInfoButton.ActualHeight > 0
            ? SiteInfoButton.TranslatePoint(new Point(SiteInfoButton.ActualWidth / 2, SiteInfoButton.ActualHeight), RootBorder)
            : PageFrame.TranslatePoint(new Point(32, 0), RootBorder);
        _saveBubble.Margin = new Thickness(Math.Max(8, anchor.X - 23), anchor.Y + 2, 0, 0);
    }

    // «Agora não» (ou ×, ou Esc): a bolha fecha, a oferta fica no ícone de chave até à próxima navegação.
    private void CloseSaveBubble()
    {
        if (_saveBubble is null) return;
        _saveBubble.Forget();
        _saveBubble.Visibility = Visibility.Collapsed;
    }

    private void DropSaveOffer(BrowserTab tab)
    {
        _vaultOffers.Remove(tab);
        if (ReferenceEquals(tab, Tabs.Active)) CloseSaveBubble();
        UpdatePasswordKey();
    }

    private void ForgetSaveState(BrowserTab tab)
    {
        _vaultCandidates.Remove(tab);
        if (_vaultOffers.ContainsKey(tab)) DropSaveOffer(tab);
    }

    private void SaveOfferedPassword(string username)
    {
        if (Tabs.Active is not { } tab || !_vaultOffers.TryGetValue(tab, out var entry)) return;
        var offer = entry.Offer;
        try
        {
            var vault = VaultService.Profile;
            var old = offer.ExistingId is { } id ? vault.GetLogin(id) : null;
            vault.SaveLogin(old is not null
                ? old with { Username = username, Password = offer.Password }
                : new LoginItem(string.Empty, offer.Origin, username, offer.Password, null, default, default, null, null, false, null));
        }
        catch (Exception)
        {
            ShowToast("Não foi possível guardar a palavra-passe.");
            return;
        }
        DropSaveOffer(tab);
        ShowToast(offer.Kind == SaveOfferKind.Update ? "Palavra-passe atualizada" : "Palavra-passe guardada");
    }

    private void NeverSaveHere()
    {
        if (Tabs.Active is not { } tab || !_vaultOffers.TryGetValue(tab, out var entry)) return;
        try { VaultService.Profile.AddNeverSave(entry.Offer.Origin); }
        catch (Exception) { return; }
        DropSaveOffer(tab);
        ShowToast($"Não volto a oferecer guardar palavras-passe em {Origins.DisplayHost(entry.Offer.Origin) ?? entry.Offer.Origin}");
    }

    // Uma palavra-passe gerada (C5) foi enviada com sucesso: deixa de estar «Não confirmada», com o utilizador do formulário.
    private void ConfirmGeneratedPassword(SaveOffer offer)
    {
        try
        {
            if (offer.ExistingId is not { } id || VaultService.Profile.GetLogin(id) is not { } login) return;
            VaultService.Profile.SaveLogin(login with { Username = offer.Username, Generated = false });
        }
        catch (Exception) { return; }
        ShowToast($"Palavra-passe gerada guardada para {Origins.DisplayHost(offer.Origin) ?? offer.Origin}");
    }

    private void HookSaveBubble()
    {
        if (_saveHooked) return;
        _saveHooked = true;
        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TabsViewModel.Active)) return;
            CloseSaveBubble();
            UpdatePasswordKey();
        };
        SizeChanged += (_, _) => PlaceSaveBubble();
        // Esc com a bolha aberta (o foco na janela ou na bolha) é «Agora não».
        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _saveBubble is not { Visibility: Visibility.Visible }) return;
            e.Handled = true;
            CloseSaveBubble();
        }
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        _overlay?.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
    }

    // O ícone de chave na barra de endereço: a aba à vista tem uma oferta por responder.
    private void UpdatePasswordKey()
        => PasswordKeyButton.Visibility = Tabs.Active is { } tab && _vaultOffers.ContainsKey(tab) ? Visibility.Visible : Visibility.Collapsed;

    private void PasswordKey_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab) return;
        if (_saveBubble is { Visibility: Visibility.Visible }) CloseSaveBubble();
        else ShowSaveBubble(tab);
    }
}

// O que as páginas do cofre pedem ao processo do navegador, feito na thread da interface: a janela dona, os diálogos
// de ficheiro nativos (o CSV nunca entra na página), a confirmação de uma pasta na nuvem, a Reciclagem, a área de
// transferência segura e o esconder das capturas.
internal sealed class WindowsVaultHost : IVaultHost
{
    private static System.Windows.Threading.Dispatcher Ui => Application.Current.Dispatcher;

    private static MainWindow? Owner(int browserId)
        => MainWindow.WindowOf(browserId) ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive);

    public Task<nint> WindowAsync(int browserId)
        => Ui.InvokeAsync(() => Owner(browserId) is { } window ? new WindowInteropHelper(window).Handle : 0).Task;

    public CancellationToken WhileInFront(int browserId) => MainWindow.VaultFrontToken();

    public Task<string?> PickImportFileAsync(int browserId) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar palavras-passe",
            Filter = "Ficheiros CSV (*.csv)|*.csv|Todos os ficheiros (*.*)|*.*",
            CheckFileExists = true,
        };
        return (Owner(browserId) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }).Task;

    public Task<string?> PickExportFileAsync(int browserId, string suggestedName) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar palavras-passe",
            Filter = "Ficheiros CSV (*.csv)|*.csv",
            FileName = suggestedName,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return (Owner(browserId) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }).Task;

    public Task<bool> ConfirmAsync(int browserId, string title, string message) => Ui.InvokeAsync(() =>
    {
        var answer = Owner(browserId) is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }).Task;

    public bool Recycle(string path) => RecycleBin.Send(path);

    public bool CopySecret(string text) => SecureClipboard.Shared.Copy(text);

    public IDisposable ShowSecret() => SecretOnScreen.Show();

    public void OpenTab(int browserId, string url) => Ui.InvokeAsync(() => Owner(browserId)?.OpenTabFromVault(url));

    public IReadOnlyDictionary<string, string> Icons(IEnumerable<string> urls) => AppData.SiteIcons(urls);

    public UserSettings Settings => Data.Settings.Current;

    public bool SetSetting(string key, string value) => Data.Settings.Set(key, value);

    public IDisposable Schedule(TimeSpan delay, Action run) => Timers.Schedule(delay, run);
}

// «Enviar o ficheiro CSV para a Reciclagem» depois de importar (o ficheiro tem todas as palavras-passe em claro).
internal static class RecycleBin
{
    private const uint Delete = 3;
    private const ushort AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        public int Aborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);

    public static bool Send(string path)
    {
        // A lista de ficheiros termina com dois nulos (o marshaller junta um).
        var operation = new FileOperation { Function = Delete, From = path + "\0", Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi) };
        return SHFileOperation(ref operation) == 0 && operation.Aborted == 0 && !System.IO.File.Exists(path);
    }
}
