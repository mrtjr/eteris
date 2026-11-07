using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Revisão aos 50: «Esconder do Recall e de capturas» também nas janelas de topo que abrem depois de ligar — os Popups
// do WPF (sugestões com o histórico, cadeado, lista de abas, menus) têm uma janela própria que não é uma Window.
public class CaptureShieldPopupTests
{
    [Fact]
    public void PopupOpenedAfterwards_IsHiddenFromCapture()
    {
        var excluded = false;
        var hadWindow = false;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                CaptureShield.WantedOverride = () => CaptureShield.WdaExcludeFromCapture;
                CaptureShield.HookNewWindows();
                var popup = new Popup
                {
                    Child = new TextBlock { Text = "sugestões do histórico", Width = 120, Height = 40 },
                    Placement = PlacementMode.AbsolutePoint,
                    HorizontalOffset = 20,
                    VerticalOffset = 20,
                };
                popup.IsOpen = true;
                // O evento do Windows chega pela fila de mensagens desta thread: espera-se por ele (com a máquina
                // ocupada pela bateria inteira pode demorar), até 10 s.
                var frame = new DispatcherFrame();
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
                {
                    if (PresentationSource.FromVisual(popup.Child) is HwndSource source)
                    {
                        hadWindow = true;
                        excluded = CaptureShield.IsExcluded(source.Handle);
                    }
                    if (excluded || DateTime.UtcNow - started > TimeSpan.FromSeconds(10)) frame.Continue = false;
                }, Dispatcher.CurrentDispatcher);
                Dispatcher.PushFrame(frame);
                timer.Stop();
                popup.IsOpen = false;
            }
            catch (Exception e) { error = e; }
            finally
            {
                CaptureShield.WantedOverride = null;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Null(error);
        Assert.True(hadWindow);
        Assert.True(excluded);
    }

    [Fact]
    public void Install_HooksEveryNewTopLevelWindowOfTheProcess()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "CaptureShield.cs"));
        Assert.Contains("HookNewWindows();", source);
        Assert.Contains("SetWinEventHook(EventObjectCreate, EventObjectShow, 0, s_hook, (uint)Environment.ProcessId, 0, WinEventOutOfContext)", source);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
