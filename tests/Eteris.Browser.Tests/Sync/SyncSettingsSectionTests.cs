using Eteris.Browser.Sync;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// Definições › Sincronização mais curta (0.2.15, T14): com sessão, o estado numa linha à frente; sem sessão, título,
// uma frase, um botão e uma ligação; o resto recolhido em «Mais opções», sem nunca esconder o que pede atenção.
public class SyncSettingsSectionTests
{
    private const long Now = 1_762_560_000_000;
    private const long Minute = 60_000;

    // ---- a linha de estado (SyncService.Headline) --------------------------------------------------------------------

    [Fact]
    public void Headline_SaysConnectedAndWhenItLastSynced()
    {
        Assert.Equal("Ligada · última sincronização há 2 min", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 2 * Minute), Now));
        Assert.Equal("Ligada · última sincronização agora mesmo", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 10_000), Now));
        Assert.Equal("Ligada · última sincronização há 3 h", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 180 * Minute), Now));
    }

    // Um computador à espera de aprovação, a escolher, parado ou desligado não está «Ligada»: fala o item próprio.
    [Fact]
    public void Headline_IsNull_WhenThereIsSomethingElseToSay()
    {
        var last = Now - 2 * Minute;
        Assert.Null(SyncService.Headline(null, Now));
        foreach (var phase in new[] { SyncPhase.Off, SyncPhase.Choose, SyncPhase.Pairing, SyncPhase.Stopped })
            Assert.Null(SyncService.Headline(TestStatus.Of(phase, last: last), Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { NeedsSignIn = true }, Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { Deleting = true }, Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last, error: "x"), Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { NeedsUpdate = true }, Now));
    }

    [Fact]
    public void Headline_WhileStarting_WhileRunning_AndBeforeTheFirstSync()
    {
        Assert.Equal("A ligar…", SyncService.Headline(TestStatus.Of(SyncPhase.Starting), Now));
        Assert.Equal("A sincronizar…", SyncService.Headline(TestStatus.Of(SyncPhase.On, running: true, last: Now - 2 * Minute), Now));
        Assert.Equal("Ligada · a primeira sincronização está a caminho", SyncService.Headline(TestStatus.Of(SyncPhase.On), Now));
    }

    [Fact]
    public void StatusData_CarriesTheHeadline_AndKeepsTheOldFields()
    {
        var data = SyncService.StatusData(TestStatus.Of(SyncPhase.On, last: Now - 2 * Minute), Now).ToString()!;
        Assert.Contains("headline = Ligada · última sincronização há 2 min", data, StringComparison.Ordinal);
        Assert.Contains("lastText = ", data, StringComparison.Ordinal);
        Assert.Contains("summary = ", data, StringComparison.Ordinal);
    }
}
