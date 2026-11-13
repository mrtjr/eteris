namespace Eteris.Browser.Infrastructure;

// O que um lançamento faz quando já há (ou ainda há) um Éteris neste perfil (INF-05). Antes: tentava a instância única uma
// vez, enviava os argumentos uma vez e, se o envio falhasse, desistia em silêncio — um lançamento feito enquanto o Éteris
// anterior ainda fechava (o mutex ainda preso, o canal já fechado) perdia-se, link incluído. Agora espera até Total que
// uma das duas coisas aconteça: a instância antiga acabar (este lançamento fica como a principal) ou o envio resultar.
// Regra pura, sem relógio nem pipes (tudo vem de fora), para se poder testar.
public static class InstanceHandoff
{
    public static readonly TimeSpan Total = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);

    // «acquire»: tenta ser a instância única (nulo: há outra); «send»: entrega os argumentos à que está aberta;
    // «elapsed»: quanto tempo passou desde o início; «pause»: espera um pouco entre tentativas.
    // Devolve a instância (se este lançamento passou a ser a principal) e se os argumentos foram entregues a outra.
    // (nulo, falso): nem uma nem outra em Total — o chamador activa a janela existente e sai.
    public static (T? Instance, bool Delivered) Run<T>(Func<T?> acquire, Func<bool> send, Func<TimeSpan> elapsed, Action<TimeSpan> pause)
        where T : class
    {
        while (true)
        {
            if (acquire() is { } instance) return (instance, false);
            if (send()) return (null, true);
            if (elapsed() >= Total) return (null, false);
            pause(Pause);
        }
    }
}
