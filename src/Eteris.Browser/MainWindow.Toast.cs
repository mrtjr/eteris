using System.Windows;
using System.Windows.Media.Animation;

namespace Eteris.Browser;

public partial class MainWindow
{
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
