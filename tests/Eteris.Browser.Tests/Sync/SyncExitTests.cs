using System.IO;
using Eteris.Browser.Sync;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// As saídas da conta (T12): «Sair deste computador» (só local), «Retirar o acesso do Éteris à conta Google» (revoga),
// «Perdi um computador / foi roubado» e «Começar do zero…». A lógica é a de SyncExit (sem estado nem estáticos): os
// testes usam agentes de teste e delegados, nunca o SyncService nem a Google real.
public sealed class SyncExitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-sync-exit-" + Guid.NewGuid().ToString("N"));
    private int _stops;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private void StopHere() => _stops++;

    private static Func<CancellationToken, Task<RevokeOutcome>> Answer(RevokeOutcome outcome) => _ => Task.FromResult(outcome);

    // ---- textos -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Texts_AreThePlanTexts()
    {
        Assert.Equal("Sai só deste computador. A Google continua a listar o Éteris na sua conta até usar «Retirar o acesso».", SyncExit.SignOutHelp);
        Assert.Equal("Retirar o acesso do Éteris à conta Google", SyncExit.RevokeTitle);
        Assert.Equal("Revoga a autorização na Google e desliga todos os seus computadores. Também em myaccount.google.com/permissions.", SyncExit.RevokeText);
        Assert.Equal("Perdi um computador / foi roubado", SyncExit.LostTitle);
        Assert.Equal("Entre outra vez nos seus outros computadores (3 ações em cada) e guarde o kit novo: o antigo deixou de valer.", SyncExit.LostDone);
        Assert.Equal("A conta antiga na nuvem é apagada. O que só estava nela perde-se. O que está neste computador fica e passa para a conta nova.", SyncExit.StartOverText);
        Assert.Equal("O aparelho removido já tinha as suas palavras-passe: mude as dos sites importantes.", SyncExit.AdvicePasswords);
        Assert.Contains("ficam neste computador", SyncExit.SignOutKeeps);
    }

    // ---- «Retirar o acesso» -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_Confirmed_StopsHere_Once()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.Revoked), StopHere, CancellationToken.None);
        Assert.True(done);
        Assert.Equal(1, _stops);
        Assert.Equal(SyncExit.RevokeDone, message);
    }

    // R6: «invalid_token» só diz que o token DESTE computador já não valia; a autorização pode continuar viva.
    [Fact]
    public async Task AlreadyInvalid_NeverSaysWithdrawn()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.AlreadyInvalid), StopHere, CancellationToken.None);
        Assert.False(done);
        Assert.Equal(1, _stops); // a sessão daqui ficou apagada: a sincronização para aqui
        Assert.Equal(SyncExit.RevokeUnconfirmed, message);
        Assert.NotEqual(SyncExit.RevokeDone, message);
        foreach (var text in new[] { SyncExit.RevokeUnconfirmed, SyncExit.LostUnconfirmed })
        {
            Assert.DoesNotContain("foi retirado:", text);
            Assert.DoesNotContain(SyncExit.LostDone, text);
            Assert.Contains("myaccount.google.com/permissions", text);
            Assert.Contains("não foi possível confirmar", text);
            Assert.DoesNotContain("https://", text);
        }
        // Só a mensagem da revogação confirmada diz «foi retirado:» (LostHalfway diz «ainda NÃO foi retirado:»).
        var withdrawn = typeof(SyncExit).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                && System.Text.RegularExpressions.Regex.IsMatch((string)f.GetRawConstantValue()!, "(?<!não )foi retirado:"))
            .Select(f => f.Name).ToList();
        Assert.Equal([nameof(SyncExit.RevokeDone)], withdrawn);
    }

    [Fact]
    public async Task Revoke_Failed_DoesNotStop_AndSaysItWasNotWithdrawn()
    {
        var (done, message) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.Failed), StopHere, CancellationToken.None);
        Assert.False(done);
        Assert.Equal(0, _stops);
        Assert.Contains("myaccount.google.com/permissions", message);
        Assert.Contains("não foi retirado", message);
        Assert.DoesNotContain("foi retirado:", message);

        // O limite de tempo (ou outro cancelamento) conta como falha.
        var (cancelled, cancelledMessage) = await SyncExit.RevokeAsync(_ => throw new OperationCanceledException(), StopHere, CancellationToken.None);
        Assert.False(cancelled);
        Assert.Equal(message, cancelledMessage);
        Assert.Equal(0, _stops);

        var (noSession, noSessionMessage) = await SyncExit.RevokeAsync(Answer(RevokeOutcome.NotSignedIn), StopHere, CancellationToken.None);
        Assert.False(noSession);
        Assert.Equal(SyncExit.RevokeNoSession, noSessionMessage);
        Assert.Equal(0, _stops);
    }

    // ---- a página -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExitPage_HasTheTexts_AndTheNoteOutsideTheBody()
    {
        var page = GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html");
        foreach (var text in new[] { SyncExit.SignOutHelp, SyncExit.SignOutKeeps, SyncExit.RevokeTitle, SyncExit.RevokeText })
            Assert.Contains(text, page);
        var body = page.IndexOf("id=\"syncBody\"", StringComparison.Ordinal);
        Assert.True(body > 0);
        var help = page.IndexOf("id=\"syncOutHelp\"", StringComparison.Ordinal);
        var exitNote = page.IndexOf("id=\"syncExitNote\"", StringComparison.Ordinal);
        Assert.InRange(help, 0, body);
        Assert.InRange(exitNote, 0, body);
        Assert.Contains("id=\"googleButton\" aria-describedby=\"googleNote\"", page);
        var settingsPage = GoogleSyncTests.Src("Eteris.Browser", "Browser", "SettingsPage.cs");
        Assert.Matches(@"busy = [^\n]*ExitBusy", settingsPage);
    }
}
