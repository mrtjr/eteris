using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Watching;

namespace Eteris.Browser;

// O recorte ao vivo: uma janela pequena, sempre por cima, sem barra de título nem botão na barra de tarefas, com um pedaço
// vivo de uma página (ClipScripts.Isolate esconde o resto). Uma barra própria por cima da página: «Abrir a página»,
// «Recarregar a cada…» e «Fechar»; arrasta-se pela barra e redimensiona-se pela borda.
//
// - O motor é um ChromiumWebBrowser próprio no contexto que a janela de origem lhe dá (null: o global, que vê a sessão,
//   como os sites fixados na barra lateral), com o tratador de pedidos dela (o Escudo).
// - Não é uma aba: o sono, a pressão de memória e o «Unseen» nunca o veem — fica vivo com a janela principal minimizada.
// - Sem dona (uma janela com dona minimiza-se com ela): quem o abre fecha-o quando a sua janela fecha (MainWindow.Watch.cs).
// - Transferências, pedidos de permissão, diálogos de JavaScript e o menu de contexto ficam mudos, e uma carga nunca pede
//   o foco (Browser/SilentHandlers.cs); um link aberto noutra janela (com gesto) vai para uma aba do Éteris. O som não é
//   cortado: o pedaço está à vista.
// - No máximo ClipLayout.MaxOpen abertos em toda a aplicação; cada um no 1.º lugar livre (ClipLayout.Place).
public partial class ClipWindow : Window
{
    private static readonly List<ClipWindow> s_open = [];

    // Recortes abertos em toda a aplicação (a conta do limite).
    public static int OpenCount => s_open.Count;

    private const double BarHeight = 30, Edge = 5, Frame = 1;
    private static readonly TimeSpan IsolateLimit = TimeSpan.FromSeconds(15);

    // «Recarregar a cada…»: 0 = nunca.
    public static readonly int[] ReloadChoices = [0, 5, 15, 60];

    private readonly string _url;
    private readonly string _selector;
    private readonly string _site;
    private readonly ChromiumWebBrowser _browser;
    private readonly DispatcherTimer _reload;
    private int _reloadMinutes;
    private bool _closed;

    public int Slot { get; }

    // O endereço recortado («Esquecer ao fechar» conta-o como página aberta do site).
    public string Address => _url;

    // «size» é o tamanho da página dentro da janela (ClipLayout.Size); a janela junta-lhe a barra e a borda.
    public ClipWindow(string url, string selector, Size size, IRequestContext? context, IRequestHandler requestHandler, Action<string> openInTab)
    {
        InitializeComponent();
        _url = url;
        _selector = selector;
        _site = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url;
        Caption.Text = _site;

        Slot = ClipLayout.FreeSlot(s_open.Select(c => c.Slot));
        s_open.Add(this);
        var extraW = 2 * Edge + 2 * Frame;
        var extraH = BarHeight + Edge + 2 * Frame;
        MinWidth = ClipLayout.MinWidth + extraW;
        MinHeight = ClipLayout.MinHeight + extraH;
        Width = size.Width + extraW;
        Height = size.Height + extraH;
        var at = ClipLayout.Place(new Size(Width, Height), SystemParameters.WorkArea, Slot);
        Left = at.X;
        Top = at.Y;

        _browser = new ChromiumWebBrowser(url)
        {
            RequestHandler = requestHandler,
            LifeSpanHandler = new PopupToTabHandler(request =>
            {
                if (request.UserGesture && PageMenu.IsWebLink(request.TargetUrl)) Dispatcher.InvokeAsync(() => openInTab(request.TargetUrl));
            }),
            DownloadHandler = new SilentDownloadHandler(() => { }),
            PermissionHandler = new SilentPermissionHandler(),
            JsDialogHandler = new SilentDialogHandler(),
            MenuHandler = new SilentMenuHandler(),
            // Uma carga (a recarga marcada incluída) nunca tira o teclado à janela onde o utilizador está a escrever.
            FocusHandler = new ClipFocusHandler(),
        };
        if (context is not null) _browser.RequestContext = context;
        _browser.FrameLoadStart += (_, e) =>
        {
            // Conteúdo web confirmado: a rede de segurança do sandbox deixa de contar quedas dos filhos (INF-01), como nas abas.
            if (e.Frame.IsMain && Infrastructure.SandboxFallback.IsWebContent(e.Url)) Infrastructure.StartupGuard.Current.WebContentCommitted();
        };
        // A cada carga da página (a 1.ª, «Recarregar», a recarga marcada), o pedaço volta a ficar sozinho.
        _browser.FrameLoadEnd += (_, e) =>
        {
            if (e.Frame.IsMain) Dispatcher.InvokeAsync(IsolatePiece);
        };
        PageHost.Child = _browser;

        _reload = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _reload.Tick += (_, _) => Reload();

        // Os botões tratam o próprio clique: o resto da barra arrasta a janela.
        Bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                try { DragMove(); } catch (InvalidOperationException) { }
        };
        CloseButton.Click += (_, _) => Close();
        OpenButton.Click += (_, _) => openInTab(_url);
        ReloadButton.Click += (_, _) => ShowReloadMenu();
        // Nunca maximizada nem minimizada (sem botão na barra de tarefas, minimizada perdia-se).
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };
        Closed += (_, _) => Release();
    }

    private async void IsolatePiece()
    {
        if (_closed || _browser.IsDisposed) return;
        var address = _browser.Address;
        var result = await PageScript.EvaluateAsync(_browser, "return " + ClipScripts.Isolate(_selector) + ";", IsolateLimit, asyncBody: true);
        if (_closed || _browser.IsDisposed || _browser.Address != address) return;
        // Sem o pedaço (a página mudou, ainda não o pôs ao fim de ~10 s, ou esconde-o nesta largura), a janela mostra a
        // página como está — e di-lo.
        Caption.Text = result.Success && result.Result is string ? _site : _site + " · o pedaço não aparece: a mostrar a página";
    }

    // Volta sempre ao endereço recortado (um link seguido dentro do recorte não fica).
    public void Reload()
    {
        if (_closed || _browser.IsDisposed) return;
        _browser.Load(_url);
    }

    public void SetReload(int minutes)
    {
        _reloadMinutes = ReloadChoices.Contains(minutes) ? minutes : 0;
        _reload.Stop();
        if (_reloadMinutes == 0) return;
        _reload.Interval = TimeSpan.FromMinutes(_reloadMinutes);
        _reload.Start();
    }

    private void ShowReloadMenu()
    {
        var menu = new Infrastructure.DarkMenu().Item("", "Recarregar agora", Reload).Separator();
        foreach (var minutes in ReloadChoices)
            menu.Item(minutes == _reloadMinutes ? "" : "", minutes == 0 ? "Nunca recarregar sozinho" : $"Recarregar a cada {minutes} min",
                () => SetReload(minutes));
        menu.OpenBelow(ReloadButton);
    }

    private void Release()
    {
        if (_closed) return;
        _closed = true;
        s_open.Remove(this);
        _reload.Stop();
        PageScript.Release(_browser);
        try { if (!_browser.IsDisposed) _browser.Dispose(); } catch (Exception) { }
    }
}
