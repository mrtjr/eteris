using System.Windows.Input;

namespace Eteris.Browser.Tabs;

// Para onde vai um atalho de Espaços: o N.º (1…9, 9 = o último) ou o vizinho (−1 / +1).
public readonly record struct WorkspaceMove(int Number, int Offset)
{
    public static WorkspaceMove To(int number) => new(number, 0);
    public static readonly WorkspaceMove Next = new(0, 1);
    public static readonly WorkspaceMove Previous = new(0, -1);
}

// Atalhos dos Espaços: Ctrl+Alt+1…9 e Ctrl+Alt+←/→. Ctrl+Alt é, para o Windows, o AltGr (Alt da direita) — e
// AltGr+2 tem de continuar a escrever «@» —, por isso só conta com o Alt da esquerda físico; Shift nunca entra.
public static class WorkspaceShortcut
{
    public static WorkspaceMove? Resolve(Key key, bool shift, bool leftAltDown)
    {
        if (shift || !leftAltDown) return null;
        if (key >= Key.D1 && key <= Key.D9) return WorkspaceMove.To(key - Key.D0);
        if (key >= Key.NumPad1 && key <= Key.NumPad9) return WorkspaceMove.To(key - Key.NumPad0);
        return key switch
        {
            Key.Right => WorkspaceMove.Next,
            Key.Left => WorkspaceMove.Previous,
            _ => null,
        };
    }
}
