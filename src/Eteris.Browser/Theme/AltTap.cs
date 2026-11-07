namespace Eteris.Browser.Theme;

// «Alt sozinho» (como a barra de menus do Firefox): só conta se o Alt for largado sem nenhuma outra tecla nem
// clique pelo meio. Alt+←, Alt+Tab, Alt+F4 ou Alt+clique não chamam as barras.
public sealed class AltTap
{
    private bool _armed;
    // O Alt já está em baixo: a repetição automática da tecla não volta a armar depois de outra tecla.
    private bool _held;

    // Uma tecla foi premida: o Alt (sem Ctrl nem Shift) arma, qualquer outra desarma.
    public void KeyDown(bool isAlt, bool otherModifiers)
    {
        if (!isAlt || otherModifiers)
        {
            _armed = false;
            _held |= isAlt;
            return;
        }
        if (_held) return;
        _armed = true;
        _held = true;
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

    // Clique do rato, perda de foco: o gesto já não é «Alt sozinho».
    public void Cancel() => _armed = false;
}
