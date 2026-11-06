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

    private static readonly Brush[] BadgePalette =
        new[] { "#8B5CF6", "#0EA5C6", "#10B981", "#EC4899", "#F59E0B", "#3B82F6" }
            .Select(hex => { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; })
            .ToArray();

    public string? Monogram => SiteBadge.Letter(Address);

    public Brush MonogramBrush => BadgePalette[SiteBadge.ColorIndex(Address)];

    partial void OnAddressChanged(string value)
    {
        OnPropertyChanged(nameof(Monogram));
        OnPropertyChanged(nameof(MonogramBrush));
    }
}
