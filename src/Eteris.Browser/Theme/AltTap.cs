namespace Eteris.Browser.Theme;

// «Alt sozinho» (como a barra de menus do Firefox): só conta se o Alt for largado sem nenhuma outra tecla nem
// clique pelo meio. Alt+←, Alt+Tab, Alt+F4 ou Alt+clique não chamam as barras.
public sealed class AltTap
{
    private bool _armed;
    // O Alt já está em baixo: a repetição automática da tecla não volta a armar depois de outra tecla.
    private bool _held;

    // Último Alt premido (sozinho) ainda por usar: o Espreitar só abre um cartão por cada Alt verdadeiro, lido do
    // teclado pela janela ou pelo tratador nativo do Chromium — nunca por uma mensagem que a página possa forjar.
    public DateTime? PressedAtUtc { get; private set; }

    public bool TakeGesture(DateTime now, TimeSpan window)
    {
        if (PressedAtUtc is not { } at || now < at || now - at > window) return false;
        PressedAtUtc = null;
        return true;
    }

    // Uma tecla foi premida: o Alt (sem Ctrl nem Shift) arma, qualquer outra desarma.
    public void KeyDown(bool isAlt, bool otherModifiers, DateTime? now = null)
    {
        if (!isAlt || otherModifiers)
        {
            _armed = false;
            _held |= isAlt;
            PressedAtUtc = null;
            return;
        }
        if (_held) return;
        _armed = true;
        _held = true;
        PressedAtUtc = now ?? DateTime.UtcNow;
    }

    // Uma tecla foi largada: devolve verdadeiro quando foi o Alt, sozinho.
    public bool KeyUp(bool isAlt)
    {
        if (!isAlt) return false;
        var tap = _armed;
        _armed = false;
        _held = false;
        return tap;
    }

    // O menu do navegador fechou: os keyup das teclas que ele consumiu na página nunca chegam (o Chromium descarta-os
    // depois de um keydown tratado pelo navegador), por isso o Alt que estava em baixo deixa de contar como premido.
    public void Reset()
    {
        _armed = false;
        _held = false;
        PressedAtUtc = null;
    }

    // Clique do rato, perda de foco: o gesto já não é «Alt sozinho».
    public void Cancel()
    {
        _armed = false;
        PressedAtUtc = null;
    }
}
