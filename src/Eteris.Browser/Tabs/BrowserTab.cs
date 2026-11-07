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

    public IReadOnlyList<KeyValuePair<string, int>> BlockedHosts
        => _blockedHosts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToList();

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
        BlockedCount = 0;
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
