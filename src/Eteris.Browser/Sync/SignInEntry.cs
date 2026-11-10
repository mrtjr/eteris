namespace Eteris.Browser.Sync;

// Os pontos de entrada do «Entrar com o Google» fora das Definições e do menu (boas-vindas, oferta depois de guardar
// uma palavra-passe, paleta, eteris://passwords e eteris://autofill). Decidir se aparecem só lê disco; nada sai para a
// Google antes do clique. O progresso depois do clique é da barra da janela e da página de fim, não destes pontos.
internal static class SignInEntry
{
    public const string Label = "Entrar com o Google";
    public const string PaletteTitle = "Entrar com o Google (sincronização)";
    public const string PaletteSubtitle = "Sincronize favoritos e palavras-passe, cifrados ponta a ponta";
    public const string AfterSaveQuestion = "Ter as suas palavras-passe noutros computadores?";

    // Puro. A mesma regra da linha «Entrar com o Google…» do menu (MainWindow.BrowserMenuAccountRow == SignIn).
    public static bool Offers(bool isPrivate, bool available, bool connected) => !isPrivate && available && !connected;

    // Puro. Só ao guardar uma palavra-passe nova pela bolha, e só uma vez por perfil (a marca grava-se ao mostrar).
    public static bool OffersAfterSave(Vault.SaveOfferKind kind, bool offers, bool alreadyOffered)
        => kind == Vault.SaveOfferKind.Save && offers && !alreadyOffered;

    // Estado real (só disco).
    public static bool OffersNow(bool isPrivate)
    {
        var available = SyncService.Available;
        return Offers(isPrivate, available, available && SyncService.Connected);
    }

    // O clique: a única implementação de «Entrar com o Google» fora do menu e da barra da janela. Sem pista de
    // conta: a pista é só da reentrada (menu e barra da janela). Na pasta de teste não há conta: liga por ela.
    public static void Begin()
    {
        if (SyncService.FolderMode) SyncService.Start();
        else GoogleSync.BeginSignIn();
    }
}
