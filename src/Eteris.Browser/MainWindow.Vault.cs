using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
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
                    // Oferecer guardar e gerar: nunca na janela privada (D-3, C7), nem com o interruptor desligado.
                    () => VaultWindowPolicy.Allows(VaultAction.OfferSave, IsPrivate) && Data.Settings.Current.PasswordOffer);
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

    // O que a lista mostra para o campo: palavras-passe guardadas (C3), o gerador (C5), contactos, cartões, IBAN ou o
    // número fiscal (C6).
    private enum VaultListMode { Logins, Generate, Contacts, Cards, Ibans, TaxIds }

    // O campo com o foco numa página (o último «focus» do script): a aba, o documento e o que o script disse do campo.
    // Generated: a palavra-passe que o gerador propõe (só na memória, até ser aceite ou o foco sair).
    private sealed record VaultFocus(BrowserTab Tab, VaultContext Context, string Field, string Form, string Kind, string? FormKind, VaultRect Rect)
    {
        public VaultListMode Mode { get; init; }
        public IReadOnlyList<LoginEntry> Logins { get; init; } = [];
        public IReadOnlyList<SuggestionRow> Rows { get; init; } = [];
        public string? Generated { get; init; }
        public override string ToString() => $"VaultFocus {Mode} {Field}";
    }

    // O último clique direito num campo (mensagem «context»), para o menu de contexto (C5, C6).
    private sealed record VaultContextField(BrowserTab Tab, VaultContext Context, VaultMessage Message, DateTime At);

    private VaultContextField? _vaultContextField;

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
            // O campo mexeu-se: a lista fecha em vez de o seguir (como no Chrome e no Firefox), para a página não a
            // levar para debaixo do cursor; o ↓ volta a abri-la no sítio novo.
            case VaultMessageType.Scroll when IsVaultFocus(tab, context, message.Field):
                if (message.Rect is { } rect) _vaultFocus = _vaultFocus! with { Rect = rect };
                CloseVaultList();
                break;
            case VaultMessageType.Generate when IsVaultFocus(tab, context, message.Field):
                OnVaultGenerate(message);
                break;
            case VaultMessageType.Context:
                _vaultContextField = new VaultContextField(tab, context, message, DateTime.UtcNow);
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
        // Uma palavra-passe nova: o gerador (C5), quando chegarem as restrições do campo (mensagem «generate»).
        if (GeneratorPolicy.Offers(kind, Data.Settings.Current.PasswordOffer, IsPrivate))
        {
            _vaultFocus = new VaultFocus(tab, context, field, form, kind, message.FormKind, rect) { Mode = VaultListMode.Generate };
            HookVaultList();
            return;
        }
        // Contacto, cartão, IBAN, NIF/CPF (C6): a lista dos guardados (cartões, IBAN e documentos nunca em http).
        var autofill = AutofillPolicy.ModeFor(kind, message.FormKind, AutofillOn);
        if (autofill != AutofillMode.None)
        {
            if (!AutofillPolicy.AllowedOn(AutofillPolicy.CategoryOf(autofill), context.Origin)) return;
            IReadOnlyList<SuggestionRow> items;
            try { items = AutofillRowsFor(autofill); }
            catch (Exception) { return; }
            if (items.Count == 0) return;
            _vaultFocus = new VaultFocus(tab, context, field, form, kind, message.FormKind, rect) { Mode = ListModeOf(autofill), Rows = items };
            HookVaultList();
            _vaultCanOpen = true;
            // Sem gesto (um campo.focus() da página), a lista só abre com ↓.
            if (message.Gesture) ShowVaultList(selected: -1);
            return;
        }
        if (!VaultFillPolicy.OffersLogins(kind, message.FormKind)) return;
        IReadOnlyList<LoginEntry> logins;
        try { logins = VaultService.Profile.LoginsFor(context.Origin); }
        catch (Exception) { return; }
        if (logins.Count == 0) return;
        var rows = VaultFillPolicy.Suggestions(logins, context.Origin, kind == "username" ? message.Prefix : null);
        _vaultFocus = new VaultFocus(tab, context, field, form, kind, message.FormKind, rect) { Logins = logins, Rows = rows };
        HookVaultList();
        _vaultCanOpen = rows.Count > 0;
        if (rows.Count > 0 && message.Gesture && Data.Settings.Current.PasswordSuggestions) ShowVaultList(selected: -1);
    }

    // As restrições do campo chegaram: o gerador propõe uma palavra-passe que as cumpre (ou nada, se não der). O padrão
    // vem da página: a geração corre fora da thread da interface (e tem um prazo total, PasswordGenerator.Budget).
    private async void OnVaultGenerate(VaultMessage message)
    {
        if (_vaultFocus is not { Mode: VaultListMode.Generate } focus || !VaultWindowPolicy.Allows(VaultAction.Generate, IsPrivate)
            || !Data.Settings.Current.PasswordOffer) return;
        var rules = new PasswordRules(message.MinLength, message.MaxLength, message.Pattern);
        string? password;
        try { password = await Task.Run(() => PasswordGenerator.Generate(rules)); }
        catch (Exception) { return; }
        // O foco pode ter mudado enquanto se gerava.
        if (password is null || !ReferenceEquals(_vaultFocus, focus)) return;
        _vaultFocus = focus with { Generated = password, Rows = [GeneratorPolicy.Row(password)] };
        _vaultCanOpen = true;
        if (Data.Settings.Current.PasswordSuggestions) ShowVaultList(selected: -1);
    }

    // O utilizador escreve: no campo do utilizador, a lista filtra pelo que já escreveu; na palavra-passe, fecha.
    private void OnVaultFieldInput(VaultMessage message)
    {
        if (_vaultFocus is not { } focus) return;
        if (focus.Kind != "username" || focus.Mode != VaultListMode.Logins)
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
            _vaultList.Chosen += (index, pressedAt) =>
            {
                // O botão do rato tem de ter descido nesta linha depois de a lista aparecer (ou mudar) há pelo menos o
                // guarda: um clique logo a seguir não conta (cliques roubados).
                if (!VaultFillPolicy.AcceptsClick(pressedAt - _vaultShownAt)) return;
                _vaultList.Select(index);
                AcceptVaultSuggestion();
            };
            _vaultList.ManageRequested += () =>
            {
                CloseVaultList();
                OpenVaultPage(_vaultFocus is { } shown ? AutofillPolicy.ManageUrl(CategoryOf(shown.Mode)) : "eteris://passwords/");
            };
            AddToOverlay(_vaultList);
        }
        HookVaultList();
        _vaultList.Heading = focus.Mode switch
        {
            VaultListMode.Generate => "Palavra-passe forte",
            VaultListMode.Contacts => "Informações de contacto",
            VaultListMode.Cards => "Cartões guardados",
            VaultListMode.Ibans => "IBAN guardados",
            VaultListMode.TaxIds => "Documentos de identificação",
            _ => "Palavras-passe guardadas",
        };
        _vaultList.Footer = focus.Mode switch
        {
            VaultListMode.Contacts => "Gerir informações de contacto…",
            VaultListMode.Cards or VaultListMode.Ibans => "Gerir pagamentos…",
            VaultListMode.TaxIds => "Gerir documentos…",
            _ => "Gerir palavras-passe…",
        };
        _vaultList.Show(focus.Rows, selected);
        _vaultList.Visibility = Visibility.Visible;
        // O guarda contra cliques roubados recomeça sempre que a lista aparece ou muda de linhas.
        _vaultShownAt = DateTime.UtcNow;
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

    // Thread do CEF (ShortcutFromPage): as teclas da lista, decididas por VaultKeys (puro). null: não é do cofre. Só as
    // teclas da própria aba do campo contam (from): as de um site fixado ou da outra metade do ecrã são de lá.
    private bool? SuggestionKeyFromPage(Key key, bool ctrl, bool shift, bool alt, BrowserTab? from)
    {
        if (from is null || Volatile.Read(ref _vaultFocus) is not { } focus || !ReferenceEquals(focus.Tab, from)) return null;
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
        switch (focus.Mode)
        {
            case VaultListMode.Generate:
                _ = FillGeneratedAsync(focus, onlyThisField: false);
                break;
            case VaultListMode.Logins:
                _ = FillVaultLoginAsync(focus, row);
                break;
            default:
                _ = FillItemAsync(focus.Tab, focus.Context, focus.Field, focus.Form, focus.Kind, CategoryOf(focus.Mode), row.Id);
                break;
        }
    }

    // ---- Contactos, pagamentos, documentos e viagens (docs/palavras-passe.md, C6) ----

    private AutofillSwitches AutofillOn
        => new(Data.Settings.Current.AutofillContacts, Data.Settings.Current.AutofillPayments, Data.Settings.Current.AutofillDocuments);

    private static VaultListMode ListModeOf(AutofillMode mode) => mode switch
    {
        AutofillMode.Contacts => VaultListMode.Contacts,
        AutofillMode.Cards => VaultListMode.Cards,
        AutofillMode.Ibans => VaultListMode.Ibans,
        _ => VaultListMode.TaxIds,
    };

    private static FillCategory CategoryOf(VaultListMode mode) => mode switch
    {
        VaultListMode.Contacts => FillCategory.Contact,
        VaultListMode.Cards => FillCategory.Card,
        VaultListMode.Ibans => FillCategory.Iban,
        VaultListMode.TaxIds => FillCategory.Document,
        _ => FillCategory.Login,
    };

    // As linhas pelos metadados do cofre (os números não se decifram para a lista).
    private static IReadOnlyList<SuggestionRow> AutofillRowsFor(AutofillMode mode)
    {
        var vault = VaultService.Profile;
        IEnumerable<VaultEntry> entries = mode switch
        {
            AutofillMode.Contacts => vault.List(VaultCollection.Contacts),
            AutofillMode.Cards => vault.List(VaultCollection.Cards),
            AutofillMode.Ibans => vault.List(VaultCollection.Ibans),
            _ => vault.List(VaultCollection.IdDocs).Where(d => d.Meta["type"]?.GetValue<string>() == IdDocItem.TypeCode(IdDocType.TaxId)),
        };
        return entries.Select(AutofillRows.FromEntry).OfType<SuggestionRow>().ToList();
    }

    private static IReadOnlyList<SuggestionRow> RowsOf(VaultCollection collection)
        => VaultService.Profile.List(collection).Select(AutofillRows.FromEntry).OfType<SuggestionRow>().ToList();

    // Preencher um contacto, cartão, IBAN, documento ou viagem (lista ou menu de contexto): a aba à vista na janela ativa
    // e o mesmo documento; cartões, IBAN e documentos nunca em http e com a reautenticação (D-5, «Pedir o Windows Hello ao
    // preencher pagamentos», ligado por omissão); o item decifra-se agora. Num campo do tipo da categoria, o formulário
    // inteiro desse campo (os campos que o utilizador já escreveu ficam; o CVC fica vazio e com o foco); noutro campo
    // (menu de contexto), só esse campo com o valor principal.
    private async Task FillItemAsync(BrowserTab tab, VaultContext context, string field, string form, string? kind, FillCategory category, string id)
    {
        if (VaultChannelOf(tab) is not { } channel) return;
        bool Allowed() => VaultFillPolicy.CanFill(new VaultFillPolicy.FillCheck(IsTabOnScreen(tab), IsActive || _overlay?.IsActive == true,
            channel.IsCurrent(context), context.Origin, context.Origin)) == FillVerdict.Allowed;
        if (!Allowed()) return;
        if (!AutofillPolicy.AllowedOn(category, context.Origin))
        {
            ShowToast("Cartões, IBAN e documentos só se preenchem em ligações seguras (https).");
            return;
        }
        if (AutofillPolicy.Reauth(category) is { } purpose
            && ReauthPolicy.Needs(purpose, Data.Settings.Current.PasswordAskHello, Data.Settings.Current.PaymentsAskHello)
            && !await ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken()))
            return;
        if (!Allowed()) return;
        object? item;
        try
        {
            var vault = VaultService.Profile;
            item = category switch
            {
                FillCategory.Contact => vault.GetContact(id),
                FillCategory.Card => vault.GetCard(id),
                FillCategory.Iban => vault.GetIban(id),
                FillCategory.Document => vault.GetIdDoc(id),
                _ => vault.GetTravel(id),
            };
        }
        catch (Exception) { return; }
        if (item is null) return;
        Dictionary<string, string>? values = AutofillPolicy.FillsForm(category, kind) ? item switch
        {
            ContactItem c => AutofillValues.Contact(c),
            CardItem c => AutofillValues.Card(c),
            IbanItem i => AutofillValues.Iban(i),
            IdDocItem { Type: IdDocType.TaxId } d => AutofillValues.TaxId(d),
            _ => null,
        } : null;
        object argument = values is not null
            ? new { field, form, values, keep = category is FillCategory.Contact or FillCategory.Card }
            : new { field, form, value = AutofillValues.Single(item) };
        var result = await channel.CallAsync(context, "fill", argument);
        values?.Clear();
        if (!result.Success || result.Result is not double filled || filled < 1)
            ShowToast("Não foi possível preencher este campo.");
        else RecordVaultUse(category switch
        {
            FillCategory.Contact => VaultCollection.Contacts,
            FillCategory.Card => VaultCollection.Cards,
            FillCategory.Iban => VaultCollection.Ibans,
            FillCategory.Document => VaultCollection.IdDocs,
            _ => VaultCollection.Travel,
        }, id);
    }

    // «Preencher com… ›» no menu de contexto de um campo: palavra-passe, contacto, cartão (e IBAN), documento, viagem. Cada
    // submenu só abre o cofre quando é aberto.
    private void AddFillWithMenu(DarkMenu menu, VaultContextField f)
    {
        var origin = f.Context.Origin;
        var kind = f.Message.Kind;
        void Rows(DarkMenu sub, VaultCollection collection, FillCategory category)
        {
            foreach (var row in RowsOf(collection))
            {
                var id = row.Id;
                sub.Item(row.Glyph ?? string.Empty, row.Detail.Length > 0 ? $"{row.Title} · {row.Detail}" : row.Title,
                    () => _ = FillItemAsync(f.Tab, f.Context, f.Message.Field!, f.Message.Form!, kind, category, id));
            }
        }
        void Secure(DarkMenu sub, Action<DarkMenu> fill)
        {
            if (AutofillPolicy.AllowedOn(FillCategory.Card, origin)) fill(sub);
            else sub.Item("\uE72E", "Só em ligações seguras (https)", () => { }, enabled: false);
        }
        menu.Submenu("\uE8D7", "Palavra-passe", Guarded(sub => FillWithLogins(sub, f)));
        menu.Submenu("\uE77B", "Contacto", Guarded(sub => Rows(sub, VaultCollection.Contacts, FillCategory.Contact)));
        menu.Submenu("\uE8C7", "Cartão", Guarded(sub => Secure(sub, s =>
        {
            Rows(s, VaultCollection.Cards, FillCategory.Card);
            Rows(s, VaultCollection.Ibans, FillCategory.Iban);
        })));
        menu.Submenu("\uE779", "Documento", Guarded(sub => Secure(sub, s => Rows(s, VaultCollection.IdDocs, FillCategory.Document))));
        // «Preencher automaticamente viagens» (eteris://autofill): desligado, a entrada sai do menu.
        if (Data.Settings.Current.AutofillTravel)
            menu.Submenu("\uE804", "Viagem", Guarded(sub => Rows(sub, VaultCollection.Travel, FillCategory.Travel)));
    }

    // O submenu lê o cofre quando abre: um cofre que não abre neste computador (chave em falta, DPAPI, base de dados
    // ocupada) dá uma linha desativada e um aviso, em vez de uma exceção num tratador do WPF (que fechava o Éteris).
    private Action<DarkMenu> Guarded(Action<DarkMenu> fill) => sub =>
    {
        try { fill(sub); }
        catch (Exception e) when (VaultSyncProfile.IsVaultFailure(e))
        {
            sub.Item("\uE783", "O cofre não abre neste computador", () => { }, enabled: false);
            ShowToast(e is VaultUnavailableException ? e.Message : "O cofre não abre neste computador.");
        }
    };

    // As palavras-passe deste site primeiro; as de outros sites (até 15) pedem confirmação com os dois nomes.
    private void FillWithLogins(DarkMenu sub, VaultContextField f)
    {
        var vault = VaultService.Profile;
        var origin = f.Context.Origin;
        var mine = vault.LoginsFor(origin);
        foreach (var login in mine)
        {
            var id = login.Id;
            sub.Item("", $"{(login.Username.Length > 0 ? login.Username : VaultFillPolicy.NoUsername)} · {Origins.DisplayHost(login.Origin)}",
                () => _ = FillLoginFromMenuAsync(f, id, otherSite: false));
        }
        var others = vault.Logins()
            .Where(l => VaultFillPolicy.OffersElsewhere(l.Origin, origin) && Origins.Match(l.Origin, origin) == OriginMatch.None)
            .OrderBy(l => Origins.DisplayHost(l.Origin), StringComparer.CurrentCulture).Take(15).ToList();
        if (others.Count == 0) return;
        if (mine.Count > 0) sub.Separator();
        sub.Item("", "De outros sites (pede confirmação):", () => { }, enabled: false);
        foreach (var login in others)
        {
            var id = login.Id;
            sub.Item("", $"{(login.Username.Length > 0 ? login.Username : VaultFillPolicy.NoUsername)} · {Origins.DisplayHost(login.Origin)}",
                () => _ = FillLoginFromMenuAsync(f, id, otherSite: true));
        }
    }

    private async Task FillLoginFromMenuAsync(VaultContextField f, string id, bool otherSite)
    {
        if (VaultChannelOf(f.Tab) is not { } channel || f.Message is not { Field: { } field, Form: { } form }) return;
        LoginItem? login;
        try { login = VaultService.Profile.GetLogin(id); }
        catch (Exception) { return; }
        if (login is null) return;
        var saved = login.Origin;
        if (otherSite)
        {
            if (MessageBox.Show(this, VaultFillPolicy.OtherSiteQuestion(login.Origin, f.Context.Origin), "Usar noutro site?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            saved = f.Context.Origin;
        }
        bool Allowed() => VaultFillPolicy.CanFill(new VaultFillPolicy.FillCheck(IsTabOnScreen(f.Tab), IsActive || _overlay?.IsActive == true,
            channel.IsCurrent(f.Context), saved, f.Context.Origin)) == FillVerdict.Allowed;
        if (!Allowed()) return;
        if (VaultFillPolicy.NeedsReauth(Data.Settings.Current.PasswordAskHello)
            && !await ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, ReauthPurpose.FillPassword, VaultFrontToken()))
            return;
        if (!Allowed()) return;
        var values = new Dictionary<string, string> { ["username"] = login.Username, ["password"] = login.Password };
        object argument = AutofillPolicy.FillsForm(FillCategory.Login, f.Message.Kind)
            ? new { field, form, values }
            : new { field, form, value = f.Message.Kind is "new-password" ? login.Password : login.Username };
        var result = await channel.CallAsync(f.Context, "fill", argument);
        values.Clear();
        if (!result.Success || result.Result is not double filled || filled < 1)
            ShowToast("Não foi possível preencher este campo.");
        else RecordVaultUse(VaultCollection.Logins, id);
    }

    // ---- O gerador (docs/palavras-passe.md, C5) ----

    // Aceitar a palavra-passe forte: guarda-se logo no cofre, «Não confirmada» (para não se perder se a página falhar),
    // e preenche a nova e a confirmação desse formulário (ou, pelo menu de contexto noutro campo, só esse). O envio com
    // sucesso confirma-a (C4). Nunca na janela privada (D-3).
    private async Task FillGeneratedAsync(VaultFocus focus, bool onlyThisField)
    {
        if (focus.Generated is not { } password || !VaultWindowPolicy.Allows(VaultAction.Generate, IsPrivate) || !Data.Settings.Current.PasswordOffer
            || VaultChannelOf(focus.Tab) is not { } channel) return;
        if (VaultFillPolicy.CanFill(new VaultFillPolicy.FillCheck(IsTabOnScreen(focus.Tab), IsActive || _overlay?.IsActive == true,
                channel.IsCurrent(focus.Context), focus.Context.Origin, focus.Context.Origin)) != FillVerdict.Allowed) return;
        try { VaultService.Profile.SaveGenerated(focus.Context.Origin, password); }
        catch (Exception)
        {
            ShowToast("Não foi possível guardar a palavra-passe gerada.");
            return;
        }
        object argument = onlyThisField
            ? new { field = focus.Field, form = focus.Form, value = password }
            : new { field = focus.Field, form = focus.Form, values = new Dictionary<string, string> { ["new-password"] = password } };
        var result = await channel.CallAsync(focus.Context, "fill", argument);
        if (!result.Success || result.Result is not double filled || filled < 1)
            ShowToast("Não foi possível preencher este campo. A palavra-passe gerada ficou em «Palavras-passe», por confirmar.");
    }

    // O clique direito acabou de chegar deste campo (o menu espera por ele até ~250 ms: a mensagem vem por outro canal).
    private VaultContextField? FreshVaultContext(BrowserTab tab, DateTime asked)
        => _vaultContextField is { } f && ReferenceEquals(f.Tab, tab) && f.At >= asked - TimeSpan.FromSeconds(1) ? f : null;

    private async Task<PageContext> WithVaultField(BrowserTab tab, PageContext context)
    {
        if (!context.IsEditable || !context.IsWebPage || VaultChannelOf(tab) is null) return context;
        var asked = DateTime.UtcNow;
        for (var i = 0; i < 8 && FreshVaultContext(tab, asked) is null; i++) await Task.Delay(30);
        if (FreshVaultContext(tab, asked) is not { } field) return context;
        return context with
        {
            Vault = new VaultFieldInfo(field.Message.Kind is "password" or "new-password",
                VaultWindowPolicy.Allows(VaultAction.Generate, IsPrivate) && Data.Settings.Current.PasswordOffer),
        };
    }

    // «Gerar palavra-passe forte» (menu de contexto num campo de palavra-passe).
    private async Task GenerateFromMenuAsync(BrowserTab tab)
    {
        if (_vaultContextField is not { } f || !ReferenceEquals(f.Tab, tab) || f.Message is not { Field: { } field, Form: { } form } m) return;
        var rules = new PasswordRules(m.MinLength, m.MaxLength, m.Pattern);
        if (await Task.Run(() => PasswordGenerator.Generate(rules)) is not { } password)
        {
            ShowToast("Este campo não aceita uma palavra-passe gerada.");
            return;
        }
        var focus = new VaultFocus(tab, f.Context, field, form, m.Kind ?? "password", null, m.Rect ?? new VaultRect(0, 0, 0, 0))
        {
            Mode = VaultListMode.Generate,
            Generated = password,
        };
        await FillGeneratedAsync(focus, onlyThisField: m.Kind != "new-password");
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
        else RecordVaultUse(VaultCollection.Logins, entry.Id);
    }

    // ---- Janelas privadas (docs/palavras-passe.md, C7) ----

    // A «última utilização» do item preenchido; nunca na janela privada (não deixa rasto).
    private void RecordVaultUse(VaultCollection collection, string id)
    {
        if (!VaultWindowPolicy.Allows(VaultAction.RecordUse, IsPrivate)) return;
        try { VaultService.Profile.MarkUsed(collection, id); }
        catch (Exception) { }
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
        if (!VaultWindowPolicy.Allows(VaultAction.OfferSave, IsPrivate) || !Data.Settings.Current.PasswordOffer) return;
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
        // Aparece sem levar o teclado (a página nova continua com ele, como no Chrome): o leitor de ecrã anuncia-a e o
        // F6 ou o ícone de chave levam o teclado para ela.
        if (ReferenceEquals(tab, Tabs.Active))
        {
            ShowSaveBubble(tab);
            _saveBubble?.Announce(SavePolicy.Announcement(offer));
        }
        UpdatePasswordKey();
    }

    // F6 com a bolha à vista e o teclado fora dela: o teclado vai para a bolha (o F6 seguinte vai para a barra de
    // endereço). true = foi para a bolha.
    private bool FocusSaveBubble()
    {
        if (_saveBubble is not { Visibility: Visibility.Visible } bubble || bubble.IsKeyboardFocusWithin) return false;
        if (_overlay is { IsVisible: true } overlay && !overlay.IsActive) overlay.Activate();
        bubble.FocusFirst();
        return true;
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
            // O utilizador corrigido na bolha para um que já existe neste site: atualiza esse (como o Chrome), em vez de
            // dar a outro item o mesmo site e utilizador.
            var old = vault.FindLogin(offer.Origin, username) is { } same ? vault.GetLogin(same)
                : offer.ExistingId is { } id ? vault.GetLogin(id) : null;
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
        // Esc com o teclado na bolha é «Agora não»; noutro sítio (a barra de endereço, um painel) o Esc é de quem o tem.
        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _saveBubble is not { Visibility: Visibility.Visible, IsKeyboardFocusWithin: true }) return;
            e.Handled = true;
            CloseSaveBubble();
        }
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        _overlay?.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
    }

    // O ícone de chave na barra de endereço: a aba à vista tem uma oferta por responder.
    private void UpdatePasswordKey()
    {
        var offer = Tabs.Active is { } tab && _vaultOffers.TryGetValue(tab, out var entry) ? entry.Offer : null;
        PasswordKeyButton.Visibility = offer is not null ? Visibility.Visible : Visibility.Collapsed;
        if (offer is null) return;
        // A dica e o nome seguem a oferta («Guardar…» ou «Atualizar…»).
        PasswordKeyButton.ToolTip = SavePolicy.KeyTip(offer);
        System.Windows.Automation.AutomationProperties.SetName(PasswordKeyButton, SavePolicy.KeyTip(offer).TrimEnd('…'));
    }

    // O ícone de chave abre a bolha e leva o teclado para ela (não a fecha: fecha-se com «Agora não», × ou Esc).
    private void PasswordKey_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab) return;
        ShowSaveBubble(tab);
        if (_saveBubble is not null)
        {
            if (_overlay is { IsVisible: true } overlay && !overlay.IsActive) overlay.Activate();
            _saveBubble.FocusFirst();
        }
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

    // O ficheiro do kit de recuperação da sincronização (0.2.15): só disco, começa em Transferências (uma pasta local
    // conhecida; o aviso de pasta vem depois, em RecoveryKit.Folder). Fora de IVaultHost: só a rota do kit o usa.
    internal static Task<string?> PickKitSaveAsync(int browserId, string suggestedName) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar o kit de recuperação",
            Filter = "Página HTML (*.html)|*.html",
            FileName = suggestedName,
            DefaultExt = ".html",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Downloads.DownloadPaths.DownloadsFolder(),
        };
        return (Owner(browserId) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }).Task;

    // «Abrir o ficheiro do kit…» no ecrã de entrada da sincronização.
    internal static Task<string?> PickKitOpenAsync(int browserId) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Abrir o ficheiro do kit",
            Filter = "Kit de recuperação (*.html)|*.html|Todos os ficheiros (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Downloads.DownloadPaths.DownloadsFolder(),
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

    public bool SyncsPasswords => Sync.SyncService.Agent() is { } agent && agent.EnabledCollections.Contains(Eteris.Sync.Model.Collections.Logins);
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
