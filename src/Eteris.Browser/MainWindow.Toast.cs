using System.Windows;
using System.Windows.Media.Animation;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private void ShowLinkStatus(BrowserTab tab, string? text)
    {
        if (!ReferenceEquals(tab, Tabs.Active) || text is null)
        {
            LinkStatusBubble.Visibility = Visibility.Collapsed;
            return;
        }
        LinkStatusText.Text = text;
        LinkStatusBubble.Visibility = Visibility.Visible;
    }

    // Aviso curto no fundo da janela: aparece, fica ~1,8 s e desvanece.
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }
}
