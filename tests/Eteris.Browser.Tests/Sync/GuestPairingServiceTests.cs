using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// O estado só de memória do computador novo no SyncService (0.2.15, T7): «Não fui eu» no alerta e o cartão aberto ou
// recolhido. Nenhum destes membros cria o agente, lê o disco ou faz rede.
[Collection(GlobalStateCollection.Name)]
public class GuestPairingServiceTests
{
    [Fact]
    public void NotMe_HidesTheAlertBar_RaisesChangedOnce()
    {
        var changed = 0;
        void count() => changed++;
        SyncService.Changed += count;
        try
        {
            SyncService.DismissReplacedAlert();
            Assert.Equal(1, changed);
            Assert.True(SyncService.ReplacedAlertDismissed);
        }
        finally { SyncService.Changed -= count; }
    }

    [Fact]
    public void GuestCard_OpenOrCollapsed_RaisesChangedOnlyWhenItChanges()
    {
        var changed = 0;
        void count() => changed++;
        SyncService.Changed += count;
        try
        {
            SyncService.SetGuestCard(true);
            var before = changed;
            SyncService.SetGuestCard(false);
            Assert.False(SyncService.GuestCardOpen);
            Assert.Equal(before + 1, changed);
            SyncService.SetGuestCard(false);
            Assert.Equal(before + 1, changed);
            SyncService.SetGuestCard(true);
            Assert.True(SyncService.GuestCardOpen);
            Assert.Equal(before + 2, changed);
        }
        finally { SyncService.Changed -= count; }
    }

    // Sem agente criado neste processo, nada a mostrar (o estado lê o campo; nunca cria o agente nem lê o perfil).
    [Fact]
    public void WithoutAnAgent_NothingToShow()
        => Assert.Equal(GuestPairingKind.None, SyncService.GuestPairingState().Kind);
}
