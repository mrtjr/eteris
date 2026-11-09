using Eteris.Browser.Data;

namespace Eteris.Browser.Tests.Sync;

// Numa janela privada, desligar o Escudo num site, o modo estrito e «esconder um elemento» ficam só em memória até a
// sessão privada acabar: nada vai para o perfil, a sincronização não é avisada (e por isso nada sobe para o Drive).
// Só toca na parte em memória do AppData (nenhuma base de dados do perfil é aberta).
public class PrivateShieldTests
{
    [Fact]
    public void PrivateWindowDecisions_StayInMemory_AndNeverReachTheSync()
    {
        var notified = 0;
        void Count() => notified++;
        AppData.SyncableChanged += Count;
        try
        {
            const string site = "visto-em-privado.example";
            AppData.SetShield(site, false, isPrivate: true);
            Assert.False(AppData.IsShieldOn(site, isPrivate: true));
            AppData.SetStrict(site, true, isPrivate: true);
            Assert.True(AppData.IsStrict(site, isPrivate: true));
            AppData.ClearHiddenElements(site, isPrivate: true);
            AppData.HideElement(site, ".anuncio", isPrivate: true);
            Assert.Equal([".anuncio"], AppData.HiddenElements(site, isPrivate: true));
            Assert.Equal(0, notified);

            // Fim da sessão privada: esquece-se tudo.
            AppData.ForgetPrivateShield();
            AppData.ClearHiddenElements(site, isPrivate: true);
            Assert.Empty(AppData.HiddenElements(site, isPrivate: true));
            AppData.ForgetPrivateShield();
            Assert.Equal(0, notified);
        }
        finally
        {
            AppData.SyncableChanged -= Count;
            AppData.ForgetPrivateShield();
        }
    }
}
