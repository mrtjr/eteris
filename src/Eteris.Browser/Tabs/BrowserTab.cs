using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

public partial class BrowserTab : ObservableObject
{
    public BrowserTab(string address) => _address = address;

    [ObservableProperty]
    private string _address;

    [ObservableProperty]
    private string _title = "Nova aba";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private int _blockedCount;

    [ObservableProperty]
    private ImageSource? _favicon;

    [ObservableProperty]
    private Color? _tint;

    [ObservableProperty]
    private bool _isSleeping;

    [ObservableProperty]
    private bool _isAudible;

    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    private readonly Dictionary<string, int> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<KeyValuePair<string, int>> BlockedHosts
        => _blockedHosts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToList();

    public void RecordBlocked(string host)
    {
        _blockedHosts[host] = _blockedHosts.GetValueOrDefault(host) + 1;
        BlockedCount++;
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

    partial void OnAddressChanged(string value)
    {
        OnPropertyChanged(nameof(Monogram));
        OnPropertyChanged(nameof(MonogramBrush));
        OnPropertyChanged(nameof(IsNewTab));
    }
}
