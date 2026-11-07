namespace Eteris.Browser.Theme;

// Interface que desaparece: em modo foco e em ecrã inteiro as barras ficam escondidas, aparecem quando o rato
// toca no topo da janela (zona quente) e voltam a esconder-se pouco depois de o rato as deixar. Um atalho de
// teclado que precise das barras (Ctrl+L, Ctrl+T) mostra-as por uns segundos. Só o estado; a janela chama
// Update a cada tique do temporizador com a posição do cursor.
public sealed class AutoHideBars
{
    // Pixels (DIP) a partir do topo da janela que revelam as barras.
    public const double HotZone = 3;
    // Tempo que as barras ficam depois de o rato as deixar (para não piscarem num movimento curto).
    public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(900);
    // Tempo que as barras ficam depois de um atalho de teclado as pedir.
    public static readonly TimeSpan KeyboardHold = TimeSpan.FromSeconds(3);

    // Modos em que as barras se escondem mas podem ser chamadas pelo rato: o modo foco e o ecrã inteiro escolhido
    // no menu. O ecrã inteiro pedido pela própria página (um vídeo) ganha sempre — mesmo vindo do modo foco o vídeo
    // ocupa tudo até se sair com Esc, como no Chrome — e a janela flutuante (modo mini) nunca mostra barras.
    public static bool Enabled(bool focusMode, bool miniMode, bool pageFullscreen, bool fullscreenByPage)
        => !miniMode && (pageFullscreen ? !fullscreenByPage : focusMode);

    private DateTime? _leftAt;
    private DateTime _holdUntil = DateTime.MinValue;

    public bool Revealed { get; private set; }

    // cursorY: posição vertical do cursor relativa ao topo da janela; inside: o cursor está dentro da janela;
    // barsHeight: altura das barras reveladas; keepOpen: há algo nas barras que precisa delas (foco na barra de
    // endereço, um menu ou painel aberto). Devolve o estado novo.
    public bool Update(double cursorY, bool inside, double barsHeight, bool keepOpen, DateTime now)
    {
        if (keepOpen || now < _holdUntil)
        {
            Revealed = true;
            _leftAt = null;
            return true;
        }
        if (!Revealed)
        {
            if (inside && cursorY >= 0 && cursorY < HotZone) Revealed = true;
            return Revealed;
        }
        var overBars = inside && cursorY >= 0 && cursorY < barsHeight;
        if (overBars)
        {
            _leftAt = null;
            return true;
        }
        _leftAt ??= now;
        if (now - _leftAt.Value >= HideDelay)
        {
            Revealed = false;
            _leftAt = null;
        }
        return Revealed;
    }

    // Um atalho pediu as barras: ficam à vista uns segundos, mesmo sem o rato lá.
    public void RevealFor(TimeSpan duration, DateTime now)
    {
        Revealed = true;
        _leftAt = null;
        _holdUntil = now + duration;
    }

    public void Hide()
    {
        Revealed = false;
        _leftAt = null;
        _holdUntil = DateTime.MinValue;
    }
}
