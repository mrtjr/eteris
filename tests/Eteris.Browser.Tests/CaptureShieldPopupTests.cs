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
// CI da v0.2.11: a thread do teste ficou mais de 20 s presa (fora da espera de 10 s, provavelmente a criar ou a fechar a
// janela do WPF sob o lock global da composição, com outros testes de janelas em paralelo). Agora corre na coleção WPF,
// sem paralelismo, pelo StaRunner (passo e tempos na mensagem), com prazo interno de 15 s e temporizador Normal.
[Collection(WpfCollection.Name)]
public class CaptureShieldPopupTests
{
    [Fact]
    public void PopupOpenedAfterwards_IsHiddenFromCapture()
    {
        var (hadWindow, excluded, log) = StaRunner.Run(steps =>
        {
            var window = false;
            var hidden = false;
            try
            {
                steps.Mark("gancho");
                CaptureShield.WantedOverride = () => CaptureShield.WdaExcludeFromCapture;
                CaptureShield.HookNewWindows();
                steps.Mark("popup.criar");
                var popup = new Popup
                {
                    Child = new TextBlock { Text = "sugestões do histórico", Width = 120, Height = 40 },
                    Placement = PlacementMode.AbsolutePoint,
                    HorizontalOffset = 20,
                    VerticalOffset = 20,
                };
                steps.Mark("popup.abrir");
                // Cria a HwndSource e a MediaContext desta thread (passa pelo lock global da composição do WPF).
                popup.IsOpen = true;
                steps.Mark("espera");
                // O evento do Windows chega pela fila de mensagens desta thread: espera-se por ele (com a máquina
                // ocupada pela bateria inteira pode demorar), até 15 s. Prioridade Normal: o trabalho de fundo, de
                // entrada ou de desenho do WPF não abafa o temporizador, que é a única saída do frame.
                var frame = new DispatcherFrame();
                var deadline = steps.Elapsed + TimeSpan.FromSeconds(15);
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) =>
                {
                    if (PresentationSource.FromVisual(popup.Child) is HwndSource source)
                    {
                        window = true;
                        hidden = CaptureShield.IsExcluded(source.Handle);
                    }
                    if (hidden || steps.Elapsed > deadline) frame.Continue = false;
                }, Dispatcher.CurrentDispatcher);
                Dispatcher.PushFrame(frame);
                timer.Stop();
                steps.Mark(hidden ? "popup.fechar" : "popup.fechar (sem afinidade)");
                popup.IsOpen = false;
            }
            finally
            {
                // Na mesma thread que o instalou: o gancho não fica para os testes seguintes.
                CaptureShield.UnhookForTests();
                CaptureShield.WantedOverride = null;
            }
            return (window, hidden, steps.Log);
        }, nameof(PopupOpenedAfterwards_IsHiddenFromCapture),
        // Presa a thread, o finally dela não corre: o resto da bateria não pode herdar «Exclude».
        onTimeout: () => CaptureShield.WantedOverride = null);
        Assert.True(hadWindow, log);
        Assert.True(excluded, log);
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
