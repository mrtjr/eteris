using System.Windows.Input;

namespace Eteris.Browser.Vault;

public enum VaultKeyAction
{
    // A tecla segue para a página.
    None,
    // ↓ num campo com sugestões e a lista fechada: abre-a.
    Open,
    Previous,
    Next,
    // Enter com uma linha escolhida: preenche.
    Accept,
    // Esc: fecha a lista (a página não vê a tecla).
    Close,
    // Tab, ou Enter sem escolha: fecha a lista e a tecla segue para a página.
    CloseAndPass,
}

// As teclas da lista de sugestões, apanhadas no processo do navegador (OnPreKeyEvent, na thread do CEF) antes de a página
// as ver (docs/palavras-passe.md, C3). Decisão pura: só com a lista aberta (ou ↓ para a abrir) e nunca com Ctrl ou Alt.
public static class VaultKeys
{
    public static VaultKeyAction FromPage(Key key, bool ctrl, bool shift, bool alt, bool listOpen, bool hasSelection, bool canOpen)
    {
        if (ctrl || alt) return VaultKeyAction.None;
        if (!listOpen) return key == Key.Down && !shift && canOpen ? VaultKeyAction.Open : VaultKeyAction.None;
        return key switch
        {
            Key.Up when !shift => VaultKeyAction.Previous,
            Key.Down when !shift => VaultKeyAction.Next,
            Key.Enter => hasSelection ? VaultKeyAction.Accept : VaultKeyAction.CloseAndPass,
            Key.Escape => VaultKeyAction.Close,
            Key.Tab => VaultKeyAction.CloseAndPass,
            _ => VaultKeyAction.None,
        };
    }

    // A página nunca vê as teclas que a lista usa.
    public static bool Consumes(VaultKeyAction action)
        => action is VaultKeyAction.Open or VaultKeyAction.Previous or VaultKeyAction.Next or VaultKeyAction.Accept or VaultKeyAction.Close;
}
