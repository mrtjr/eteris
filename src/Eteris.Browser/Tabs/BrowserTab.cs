using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

public partial class BrowserTab : ObservableObject
{
    public BrowserTab(string address) => _address = address;

    [NotifyPropertyChangedFor(nameof(Host))]
    [NotifyPropertyChangedFor(nameof(MediaSubline))]
    [ObservableProperty]
    private string _address;

    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [ObservableProperty]
    private string _title = "Nova aba";

    // Identidade dada pelo utilizador (Tabs/TabIdentity.cs): nome próprio, emoji e cor do ícone.
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [ObservableProperty]
    private string? _customTitle;

    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    [NotifyPropertyChangedFor(nameof(IconText))]
    [ObservableProperty]
    private string? _emoji;

    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    [NotifyPropertyChangedFor(nameof(IconBrush))]
    [ObservableProperty]
    private int? _colorIndex;

    // «Porque abri isto?»: uma frase presa à aba, mostrada no cartão e no Radar e guardada nas sessões.
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [ObservableProperty]
    private string? _note;

    public bool HasNote => Note is not null;

    // Faz parte da multi-seleção (Ctrl/Shift+clique) — Tabs/TabSelection.cs.
    [ObservableProperty]
    private bool _isMultiSelected;

    // Desvanecimento do arquivo automático (Tabs/ArchivePolicy.cs): 1 = visitada há pouco.
    [ObservableProperty]
    private double _ageOpacity = 1;

    // Trancada: não fecha por engano (Ctrl+W, «x», clique do meio, «fechar as outras») nem adormece.
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private bool _isLocked;

    // O que a faixa, o cartão, o radar e a paleta mostram: o nome próprio, senão o título da página.
    public string DisplayTitle => TabIdentity.DisplayTitle(CustomTitle, Title, Host);

    public bool HasCustomIcon => TabIdentity.HasCustomIcon(Emoji, ColorIndex);

    public string IconText => TabIdentity.IconText(Emoji, Monogram);

    public Brush IconBrush => ColorIndex is { } index ? TabGroup.BrushAt(index) : MonogramBrush;

    public void SetIdentity(string? name, string? emoji, int? colorIndex, string? note = null)
    {
        CustomTitle = TabIdentity.NormalizeName(name);
        Emoji = TabIdentity.NormalizeEmoji(emoji);
        ColorIndex = TabIdentity.NormalizeColor(colorIndex);
        Note = TabIdentity.NormalizeNote(note);
    }

    [ObservableProperty]
    private bool _isLoading;

    // Grupo de abas (null = sem grupo). As marcas abaixo são calculadas pelo TabsViewModel.
    [ObservableProperty]
    private TabGroup? _group;

    [ObservableProperty]
    private bool _isGroupHead;

    [ObservableProperty]
    private bool _isHiddenByGroup;

    [ObservableProperty]
    private int _groupCount;

    // Pré-visualização da página para o cartão da aba (capturada depois de carregar).
    [ObservableProperty]
    private ImageSource? _thumbnail;

    // A página parece um artigo: mostra o botão do modo de leitura.
    [ObservableProperty]
    private bool _isReaderable;

    // A aba usa o motor de vídeo (WebView2) em vez do motor principal — ver Tabs/VideoEngine.cs.
    [ObservableProperty]
    private bool _usesVideoEngine;

    // O vídeo desta página não toca no motor principal, mas a troca não foi automática (sinal fraco, DRM, leitor num
    // iframe, site excluído pelo utilizador): a razão do aviso, e a barra mostra «Tocar no motor de vídeo». Apaga-se
    // ao mudar de página.
    [ObservableProperty]
    private string? _videoOffer;

    // Voltar/Avançar entre o motor principal e o motor de vídeo (Tabs/EngineCrossing.cs).
    public EngineCrossing Crossing { get; } = new();

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private int _blockedCount;

    [ObservableProperty]
    private ImageSource? _favicon;

    internal string? FaviconSite { get; set; }

    [ObservableProperty]
    private Color? _tint;

    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private bool _isSleeping;

    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private bool _isAudible;

    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private bool _isPinned;

    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [ObservableProperty]
    private bool _isMuted;

    public string? AudioGlyph => AudioIndicator.Glyph(IsAudible, IsMuted);

    partial void OnIsAudibleChanged(bool value) => OnPropertyChanged(nameof(AudioGlyph));

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(AudioGlyph));
        OnPropertyChanged(nameof(MuteGlyph));
    }

    // ---------- Centro de multimédia ----------

    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    [ObservableProperty]
    private MediaState _media;

    [NotifyPropertyChangedFor(nameof(MediaHeadline))]
    [ObservableProperty]
    private string? _mediaTitle;

    [NotifyPropertyChangedFor(nameof(MediaSubline))]
    [ObservableProperty]
    private string? _mediaArtist;

    public string MediaHeadline => MediaTitle ?? Title;

    public string MediaSubline => MediaArtist is { } artist ? $"{artist} · {Host}" : Host;

    public string PlayPauseGlyph => Media == MediaState.Playing ? "\uE769" : "\uE768";

    public string MuteGlyph => IsMuted ? "\uE74F" : "\uE767";

    public void ApplyMedia(MediaInfo info)
    {
        Media = info.State;
        MediaTitle = info.Title;
        MediaArtist = info.Artist;
        IsAudible = info.Audible;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(MediaHeadline));

    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    // Sobe a cada mudança de endereço: uma captura em curso compara-o antes de atribuir a miniatura (MainWindow.Thumbnails.cs).
    internal int ThumbnailVersion { get; private set; }

    // Endereço da página a que a miniatura pertence.
    internal string? ThumbnailAddress { get; set; }

    private readonly Dictionary<string, int> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);

    // Observatório do site: cada domínio que a página contactou e o que o Escudo decidiu (alimentado pela thread de rede).
    public Shields.SiteObservatory Observatory { get; } = new();

    public IReadOnlyList<KeyValuePair<string, int>> BlockedHosts
        => _blockedHosts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToList();

    private readonly Dictionary<string, int> _localNetworkBlocked = new(StringComparer.OrdinalIgnoreCase);

    // Pedidos à rede local travados nesta página (Shields/LocalNetwork.cs), por destino («rede local · 192.168.1.1»).
    public IReadOnlyList<KeyValuePair<string, int>> LocalNetworkBlocked
        => _localNetworkBlocked.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToList();

    public void RecordLocalNetworkBlocked(string label) => _localNetworkBlocked[label] = _localNetworkBlocked.GetValueOrDefault(label) + 1;

    private readonly List<string> _trackingSkips = [];

    // Saltos de rastreio evitados a caminho desta página (Shields/Debounce.cs): o anfitrião de cada salto.
    public IReadOnlyList<string> TrackingSkips => _trackingSkips.ToList();

    public void RecordTrackingSkips(IEnumerable<string> hosts) => _trackingSkips.AddRange(hosts);

    private readonly List<string> _bounceSites = [];

    // Sites que só serviram de salto a caminho desta página (Shields/BounceTracking.cs). Ao contrário dos bloqueios,
    // não se limpam quando a página começa a carregar (os saltos são anteriores): só numa navegação nova do utilizador.
    public IReadOnlyList<string> BounceSites => _bounceSites.ToList();

    public void RecordBounceSites(IEnumerable<string> sites)
    {
        foreach (var site in sites)
            if (!_bounceSites.Contains(site)) _bounceSites.Add(site);
    }

    public void ClearBounceSites() => _bounceSites.Clear();

    private readonly List<string> _cloaked = [];

    // Rastreadores escondidos atrás de um subdomínio do site (CNAME) bloqueados nesta página: «nome → rastreador».
    public IReadOnlyList<string> CloakedTrackers => _cloaked.ToList();

    public void RecordCloaked(string label) => _cloaked.Add(label);

    public void RecordBlocked(string host)
    {
        _blockedHosts[host] = _blockedHosts.GetValueOrDefault(host) + 1;
        BlockedCount++;
    }

    // Cartão da aba: domínio legível e uma linha de estado.
    public string Host
    {
        get
        {
            if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) || uri.Scheme == "eteris") return "Éteris";
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        }
    }

    public string StatusLine
    {
        get
        {
            var parts = new List<string>();
            if (IsPinned) parts.Add("Fixada");
            if (IsLocked) parts.Add("Trancada");
            if (IsSleeping) parts.Add("A dormir (memória libertada)");
            if (IsMuted) parts.Add("Som silenciado");
            else if (IsAudible) parts.Add("A tocar som");
            if (BlockedCount > 0) parts.Add(BlockedCount == 1 ? "1 rastreador bloqueado" : $"{BlockedCount} rastreadores bloqueados");
            return string.Join(" · ", parts);
        }
    }

    public void ResetShield()
    {
        _blockedHosts.Clear();
        _localNetworkBlocked.Clear();
        _trackingSkips.Clear();
        _cloaked.Clear();
        BlockedCount = 0;
    }

    // As últimas páginas deixadas para trás, com a contagem do Escudo e o ícone: Voltar/Avançar pela cache de páginas
    // (back/forward cache) repõe a página tal como estava, sem pedidos novos, por isso repõe-se o que ela tinha.
    public const int RememberedPages = 8;

    private sealed record LeftPage(string Url, int Blocked, Dictionary<string, int> Hosts, ImageSource? Favicon);

    private readonly List<LeftPage> _leftPages = [];

    // A página url vai ser substituída: guarda o estado dela e começa a contagem da seguinte do zero. Chamado uma vez
    // por pedido da página nova (um redirecionamento repete-o): uma contagem já recomeçada não apaga a guardada.
    public void LeavePage(string? url)
    {
        if (url is not null && (BlockedCount > 0 || _leftPages.All(p => p.Url != url)))
        {
            _leftPages.RemoveAll(p => p.Url == url);
            _leftPages.Add(new LeftPage(url, BlockedCount, new(_blockedHosts, StringComparer.OrdinalIgnoreCase), Favicon));
            if (_leftPages.Count > RememberedPages) _leftPages.RemoveAt(0);
        }
        ResetShield();
    }

    // Voltou a url pela cache de páginas: repõe a contagem e o ícone dela. false se não há nada guardado.
    public bool ReturnToPage(string url)
    {
        var page = _leftPages.FindLast(p => p.Url == url);
        if (page is null) return false;
        _blockedHosts.Clear();
        foreach (var (host, count) in page.Hosts) _blockedHosts[host] = count;
        BlockedCount = page.Blocked;
        if (page.Favicon is not null) Favicon = page.Favicon;
        return true;
    }

    private static readonly Brush[] BadgePalette = SiteBadge.Palette
        .Select(hex => { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; })
        .ToArray();

    public string? Monogram => SiteBadge.Letter(Address);

    public Brush MonogramBrush => BadgePalette[SiteBadge.ColorIndex(Address)];

    public bool IsNewTab => Browser.NewTabPage.IsNewTab(Address);

    // Mesmo host (sem distinguir maiúsculas); páginas eteris:// comparam o nome interno (newtab, history…).
    public static bool SameHost(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
           && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);

    partial void OnAddressChanged(string? oldValue, string newValue)
    {
        ThumbnailVersion++;
        // O nome próprio era desta página/site: ao mudar de site a aba volta a chamar-se pelo título da página.
        if (!TabIdentity.KeepsIdentity(oldValue ?? string.Empty, newValue)) SetIdentity(null, null, null);
        // Só uma página de outro site apaga a miniatura: numa navegação dentro do mesmo site (pushState, #âncora)
        // o CEF não volta a disparar FrameLoadEnd, logo não haveria recaptura e a aba ficaria sem pré-visualização.
        if (!SameHost(oldValue ?? string.Empty, newValue)) Thumbnail = null;
        OnPropertyChanged(nameof(Monogram));
        OnPropertyChanged(nameof(MonogramBrush));
        OnPropertyChanged(nameof(IconText));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(IsNewTab));
    }
}
