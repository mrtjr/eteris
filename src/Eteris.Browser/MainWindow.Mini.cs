using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Janela flutuante»: a própria janela encolhe para um canto, fica sempre por cima e sem barras — a página
// continua a correr (um vídeo não recomeça). É a alternativa ao Picture-in-Picture, que não existe no modo
// de renderização fora do ecrã. Volta exatamente como estava.
public partial class MainWindow
{
    private bool _miniMode;
    private bool _focusBeforeMini;
    private Rect _beforeMiniBounds;
    private WindowState _beforeMiniState;

    public static Rect MiniBounds(Rect workArea, double width = 480, double height = 290, double margin = 16)
        => new(workArea.Right - width - margin, workArea.Bottom - height - margin, width, height);

    private void ToggleMiniWindow()
    {
        if (_miniMode) ExitMiniWindow();
        else EnterMiniWindow();
    }

    private void EnterMiniWindow()
    {
        if (_miniMode || _pageFullscreen) return;
        _miniMode = true;
        _beforeMiniState = WindowState;
        _beforeMiniBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _focusBeforeMini = _focusMode;
        ClosePalette();
        CloseSuggestions();

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        _focusMode = true;
        ApplyChromeLayout();
        MinWidth = 280;
        MinHeight = 160;
        var bounds = MiniBounds(SystemParameters.WorkArea);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        Topmost = true;
        MiniTitle.Text = Tabs.Active?.Title ?? "Éteris";
        MiniBar.Visibility = Visibility.Visible;
        // A barra fica à vista uns segundos para se perceber como voltar; depois só aparece com o rato.
        _miniBarHoldUntil = DateTime.UtcNow.AddSeconds(3);
        var intro = new DoubleAnimationUsingKeyFrames();
        intro.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        intro.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        intro.KeyFrames.Add(new LinearDoubleKeyFrame(IsMouseOver ? 1 : 0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.4))));
        MiniBar.BeginAnimation(OpacityProperty, intro);
    }

    private DateTime _miniBarHoldUntil;

    private void ExitMiniWindow()
    {
        if (!_miniMode) return;
        _miniMode = false;
        MiniBar.Visibility = Visibility.Collapsed;
        Topmost = false;
        MinWidth = 560;
        MinHeight = 400;
        _focusMode = _focusBeforeMini;
        ApplyChromeLayout();
        if (!_beforeMiniBounds.IsEmpty)
        {
            Left = _beforeMiniBounds.Left;
            Top = _beforeMiniBounds.Top;
            Width = _beforeMiniBounds.Width;
            Height = _beforeMiniBounds.Height;
        }
        WindowState = _beforeMiniState;
        Activate();
    }

    private void WatchMiniBar()
    {
        MouseEnter += (_, _) => { if (_miniMode && DateTime.UtcNow >= _miniBarHoldUntil) FadeMiniBar(1); };
        MouseLeave += (_, _) => { if (_miniMode && DateTime.UtcNow >= _miniBarHoldUntil) FadeMiniBar(0); };
        Tabs.PropertyChanged += (_, e) =>
        {
            if (_miniMode && e.PropertyName == nameof(TabsViewModel.Active)) MiniTitle.Text = Tabs.Active?.Title ?? "Éteris";
        };
    }

    private void FadeMiniBar(double to)
        => MiniBar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 400)));

    private void MiniBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ExitMiniWindow();
        else DragMove();
    }

    private void MiniRestore_Click(object sender, RoutedEventArgs e) => ExitMiniWindow();
}
