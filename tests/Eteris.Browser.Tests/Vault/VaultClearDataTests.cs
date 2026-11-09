using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// B4 (docs/palavras-passe.md): «Apagar dados de navegação» com a caixa «Palavras-passe e preenchimento automático»
// (desmarcada, com o Windows Hello) e a promessa nova «As suas palavras-passe não vão para ninguém».
public sealed class VaultClearDataTests : IDisposable
{
    private readonly VaultFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0);

    [Fact]
    public void Parse_VaultOnlyWhenAsked()
    {
        var plain = ClearDataRequest.Parse("history,cookies,cache", "all", Now)!;
        Assert.False(plain.Vault);
        var vault = ClearDataRequest.Parse("history,vault", "hour", Now)!;
        Assert.True(vault.Vault);
        Assert.True(vault.History);
        Assert.True(ClearDataRequest.Parse("vault", "all", Now)!.Vault);
        Assert.Null(ClearDataRequest.Parse("passwords", "all", Now));
    }

    [Fact]
    public void Summary_SaysTheVault()
    {
        Assert.Equal("Palavras-passe e preenchimento automático apagados", new ClearDataRequest(false, false, false, DateTime.MinValue, Vault: true).Summary);
        Assert.Equal("Histórico e cache apagados · palavras-passe e preenchimento automático também",
            new ClearDataRequest(true, false, true, DateTime.MinValue, Vault: true).Summary);
        Assert.Equal("Histórico, cookies e cache apagados", new ClearDataRequest(true, true, true, DateTime.MinValue).Summary);
    }

    // A caixa está nas Definições, desmarcada, e o texto diz que pede o Windows Hello; «Apagar ao fechar» não toca no cofre.
    [Fact]
    public void SettingsPage_VaultBoxUncheckedByDefault()
    {
        var html = SettingsPage.Render(new Data.UserSettings());
        Assert.Contains("<input type=\"checkbox\" value=\"vault\"> Palavras-passe e preenchimento automático", html);
        Assert.Contains("pede o Windows Hello", html);
        Assert.Contains("As palavras-passe e o preenchimento automático ficam.", html);
        // Recusado, o resultado não leva o ✓ e as escolhas ficam à vista.
        Assert.Contains("const done = text !== 'recusado' && !text.startsWith('Não foi');", html);
    }

    // Apagar o cofre pede a reautenticação; recusada, nada é apagado; aceite, apaga tudo (as cinco áreas).
    [Fact]
    public async Task EraseVault_NeedsReauth()
    {
        var login = _f.Vault.SaveLogin(new LoginItem(string.Empty, "https://exemplo.pt", "ana", "Senha-Forte-1!", null, default, default, null, null, false, null));
        var card = _f.Vault.Save(new CardItem(string.Empty, "Ana", "4111 1111 1111 1111", 8, 2029, null, default, null));
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        Assert.False(await _f.Routes.EraseVaultAsync(7));
        Assert.NotNull(_f.Vault.GetLogin(login));
        _f.Reauth.Outcome = ReauthOutcome.Verified;
        Assert.True(await _f.Routes.EraseVaultAsync(7));
        Assert.Null(_f.Vault.GetLogin(login));
        Assert.Null(_f.Vault.GetCard(card));
        Assert.Equal(ReauthPolicy.Message(ReauthPurpose.EraseVault), _f.Reauth.Messages[^1]);

    }

    // «Esquecer este site», «Triturar» e «Apagar ao fechar» não mexem no cofre; o apagar de dados só com a caixa.
    [Fact]
    public void Source_ForgetAndClearOnExitNeverTouchTheVault()
    {
        var src = Path.Combine(GoogleSyncTestsRoot(), "src", "Eteris.Browser");
        foreach (var file in new[] { "MainWindow.Forget.cs", "App.xaml.cs" })
        {
            var text = File.ReadAllText(Path.Combine(src, file));
            Assert.DoesNotContain("EraseVault", text);
            Assert.DoesNotContain("EraseAll", text);
        }
        Assert.Contains("As palavras-passe guardadas ficam.", File.ReadAllText(Path.Combine(src, "MainWindow.Forget.cs")));
        var factory = File.ReadAllText(Path.Combine(src, "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("if (request.Vault)", factory);
    }

    // A promessa nova, lida do estado real: o gestor do Chromium desligado no perfil, sem verificação de fugas e sem o
    // servidor de formulários da Google no motor.
    [Fact]
    public void Promise_PasswordsGoToNoOne()
    {
        var args = new Dictionary<string, string> { ["disable-features"] = string.Join(',', EngineHardening.NoGoogleOnPages.Select(f => f.Feature)) };
        PrivacyPage.Promise Find(PrivacyPage.State s) => PrivacyPage.Promises(s).Single(p => p.Id == "passwords");
        var kept = Find(State(args));
        Assert.Equal("As suas palavras-passe não vão para ninguém", kept.Title);
        Assert.True(kept.Kept);
        Assert.Contains("cofre", kept.Detail);
        Assert.False(Find(State(args) with { ChromiumPasswordsOff = false }).Kept);
        Assert.Contains("credentials_enable_service", Find(State(args) with { ChromiumPasswordsOff = false }).Detail);
        Assert.False(Find(State(args) with { LeakCheckOff = false }).Kept);
        var engineOn = Find(State(new Dictionary<string, string>()));
        Assert.False(engineOn.Kept);
        Assert.Contains("AutofillServerCommunication", engineOn.Detail);
    }

    // O estado lê de volta, do perfil, as quatro preferências do gestor do Chromium (todas falsas).
    [Fact]
    public void ChromiumPasswordPreferences_ReadBack()
    {
        Assert.Equal(["autofill.credit_card_enabled", "autofill.profile_enabled", "credentials_enable_autosignin", "credentials_enable_service"],
            PrivacyPreferences.ChromiumPasswordPreferences.Order());
        Assert.All(PrivacyPreferences.ChromiumPasswordPreferences, name => Assert.Equal(false, PrivacyPreferences.Hardening[name]));
        Assert.True(PrivacyPreferences.AreOff([false, false, false, false]));
        Assert.False(PrivacyPreferences.AreOff([false, true, false, false]));
        Assert.False(PrivacyPreferences.AreOff([false, null, false, false]));
    }

    private static PrivacyPage.State State(Dictionary<string, string> args)
        => new(args, PrivacySignals.Headers, true, true, true, null, "pt-PT", false, 1, 0);

    private static string GoogleSyncTestsRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
