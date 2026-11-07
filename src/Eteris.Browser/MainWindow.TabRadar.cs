using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Eteris.Browser;

public partial class MainWindow
{
    private Grid? _radar;
    private TabRadarView? _radarView;

    private bool RadarOpen => _radar is { Visibility: Visibility.Visible };

    private void Radar_Click(object sender, RoutedEventArgs e) => ToggleRadar();

    private void ToggleRadar()
    {

        CloseBrowserMenu();
        if (RadarOpen)
        {
            CloseRadar();
            return;
        }

        ClosePalette();
        CloseShortcuts();
        CloseSuggestions();
        CloseFind();
        CloseQr();
        ShieldPopup.IsOpen = false;
        MediaPopup.IsOpen = false;
        DownloadsPopup.IsOpen = false;
        SiteInfoPopup.IsOpen = false;

        if (_radar is null)
        {
            SetUpOverlayLayer();
            var view = new TabRadarView(Tabs, IsPrivate, ToggleMute, CloseTab, tab => OpenSideBySide(tab), CloseRadar, DoneToday,
                SnoozedTabs, snoozed => WakeSnoozed(snoozed, activate: true));
            _radarView = view;
            view.Margin = new Thickness(12, 16, 12, 16);
            var scrim = new Rectangle();
            scrim.SetResourceReference(Shape.FillProperty, "ScrimBrush");
            scrim.MouseLeftButtonDown += (_, _) => CloseRadar();

            _radar = new Grid { Visibility = Visibility.Collapsed };
            _radar.Children.Add(scrim);
            _radar.Children.Add(view);
            Grid.SetRowSpan(_radar, 3);
            Panel.SetZIndex(_radar, 115);
            WindowChrome.SetIsHitTestVisibleInChrome(_radar, true);
            KeyboardNavigation.SetTabNavigation(_radar, KeyboardNavigationMode.Cycle);
            AddToOverlay(_radar);
            Closed += (_, _) => view.Stop();
        }

        _radarView!.Start();
        _radar.Visibility = Visibility.Visible;
        FocusInOverlay(_radarView.SearchInput);
    }

    private void CloseRadar()
    {
        if (!RadarOpen) return;
        _radarView!.Stop();
        _radar!.Visibility = Visibility.Collapsed;
        ReturnFocusToPage();
    }

    private bool HandleRadarKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (key == Key.Escape)
        {
            if (!_radarView!.ClosePicker()) CloseRadar();
        }
        else if (ctrl && shift && !alt && key == Key.A) CloseRadar();
        else if (ctrl && !alt && key == Key.K) OpenPalette();
        else if (key == Key.F1) OpenShortcuts();
        else return false;
        return true;
    }
}
