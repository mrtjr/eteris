using System.Windows.Media;
using System.Windows.Threading;

namespace Eteris.Browser.Infrastructure;

// O motor de composição do WPF (o fio de desenho e o Direct3D) arranca com a 1.ª MediaContext do processo, e o fio que a
// cria fica à espera dele: no arranque eram ~100 ms do fio da interface dentro do XAML da janela (amostrado:
// WaitForSingleObjectEx chamado pelo wpfgfx a partir do PresentationCore). Uma thread à parte, com o seu próprio
// Dispatcher (como os ecrãs de arranque em thread própria), cria uma MediaContext logo no Main: o motor arranca em
// paralelo com o resto do arranque e, quando o XAML precisa dele, já está pronto. A thread termina depois da 1.ª página
// (Stop), quando a janela já tem a sua MediaContext.
public static class CompositionWarmup
{
    private static Dispatcher? _dispatcher;
    private static int _stopped;

    public static void Start()
    {
        var thread = new Thread(() =>
        {
            EventHandler noop = (_, _) => { };
            // Acrescentar um tratador ao Rendering cria a MediaContext deste Dispatcher (e com ela o motor de composição).
            CompositionTarget.Rendering += noop;
            CompositionTarget.Rendering -= noop;
            StartupTrace.Mark("wpf.motor");
            var dispatcher = Dispatcher.CurrentDispatcher;
            Volatile.Write(ref _dispatcher, dispatcher);
            if (Volatile.Read(ref _stopped) == 1)
            {
                dispatcher.InvokeShutdown();
                return;
            }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Éteris: arranque do WPF" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // Para os testes: o Dispatcher da thread (null até ela o criar).
    internal static Dispatcher? Current => Volatile.Read(ref _dispatcher);

    public static void Stop()
    {
        Volatile.Write(ref _stopped, 1);
        Volatile.Read(ref _dispatcher)?.BeginInvokeShutdown(DispatcherPriority.Normal);
    }
}
