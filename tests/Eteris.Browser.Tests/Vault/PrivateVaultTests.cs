using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C7 (docs/palavras-passe.md; desenho, secção 3.11): a janela privada lê o cofre para preencher a pedido, mas não deixa
// rasto nem guarda nada — sem «última utilização», sem bolha de guardar nem candidatos, sem gerador (D-3), e as páginas
// de gestão recusam-se (abrem numa janela normal).
public class PrivateVaultTests
{
    [Theory]
    [InlineData(VaultAction.FillOnRequest, true)]
    [InlineData(VaultAction.RecordUse, false)]
    [InlineData(VaultAction.OfferSave, false)]
    [InlineData(VaultAction.Generate, false)]
    [InlineData(VaultAction.ManagementPages, false)]
    public void PrivateWindow_OnlyFillsOnRequest(VaultAction action, bool allowed)
    {
        Assert.Equal(allowed, VaultWindowPolicy.Allows(action, isPrivate: true));
        Assert.True(VaultWindowPolicy.Allows(action, isPrivate: false));
    }

    // As decisões das outras tarefas seguem a mesma tabela.
    [Fact]
    public void PrivateWindow_NoCandidate_NoGenerator_PagesElsewhere()
    {
        var submit = new VaultMessage(VaultMessageType.Submit) { Form = "m1", FormKind = "login", Username = "ana", Password = "Inventada-1!" };
        var context = new VaultContext(1, "F", "L", "https://conta.exemplo.test", true);
        Assert.Null(SavePolicy.Candidate(submit, context, DateTimeOffset.UtcNow, offerEnabled: true, isPrivate: true));
        Assert.False(GeneratorPolicy.Offers("new-password", offerEnabled: true, isPrivate: true));
        Assert.True(VaultPages.OpensInNormalWindow(isPrivate: true));
        // No menu de contexto: «Preencher com…» sim, «Gerar palavra-passe forte» não.
        var menu = PageMenu.Build(new PageContext(null, null, "", true, false, false, IsPrivate: true) { Vault = new VaultFieldInfo(true, CanGenerate: false) }, "x");
        Assert.Contains(menu, i => i?.Command == PageCommand.FillWith);
        Assert.DoesNotContain(menu, i => i?.Command == PageCommand.GeneratePassword);
    }

    [Fact]
    public async Task PrivateWindow_ManagementPagesRefused()
    {
        using var f = new VaultFixture();
        var passwords = await f.Get("passwords", "/", isPrivate: true);
        var autofill = await f.Get("autofill", "/", isPrivate: true);
        Assert.DoesNotContain("{{", passwords.Body);
        Assert.Contains("janela normal", passwords.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("janela normal", autofill.Body, StringComparison.OrdinalIgnoreCase);
    }

    // Ler o cofre (abrir, sugerir, decifrar para preencher) não muda o ficheiro: a janela privada não deixa rasto nele.
    [Fact]
    public void ReadingTheVault_LeavesTheFileUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "eteris-vault-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string id;
            using (var vault = new VaultService(root, new Eteris.Browser.Sync.Dpapi(VaultKey.Purpose)))
                id = vault.SaveLogin(new LoginItem("", "https://conta.exemplo.test", "ana", "Inventada-1!", null, default, default, null, null, false, null));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(Path.Combine(root, VaultStore.FileName));
            using (var vault = new VaultService(root, new Eteris.Browser.Sync.Dpapi(VaultKey.Purpose)))
            {
                Assert.Single(vault.LoginsFor("https://conta.exemplo.test/entrar"));
                Assert.Equal("Inventada-1!", vault.GetLogin(id)!.Password);
                Assert.Empty(vault.List(VaultCollection.Cards));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, VaultStore.FileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // A «última utilização» (numa janela normal): só o metadado muda; a palavra-passe e o histórico ficam.
    [Fact]
    public void MarkUsed_OnlyTouchesUsed()
    {
        using var f = new VaultFixture();
        var id = f.Vault.SaveLogin(new LoginItem("", "https://conta.exemplo.test", "ana", "Inventada-1!", null, default, default, null, null, false, null));
        Assert.Null(f.Vault.GetLogin(id)!.Used);
        f.Now = f.Now.AddMinutes(3);
        f.Vault.MarkUsed(VaultCollection.Logins, id);
        var login = f.Vault.GetLogin(id)!;
        Assert.Equal(f.Now, login.Used);
        Assert.Equal("Inventada-1!", login.Password);
        Assert.Null(f.Vault.PreviousPassword(id));
        var card = f.Vault.Save(new CardItem("", "Titular", "4111111111111111", 8, 2029, null, default, null));
        f.Vault.MarkUsed(VaultCollection.Cards, card);
        Assert.Equal(f.Now, f.Vault.GetCard(card)!.Used);
        f.Vault.MarkUsed(VaultCollection.Cards, "nao-existe");
    }

    // Guarda: a janela só grava a «última utilização» por RecordVaultUse, que pergunta à tabela (nunca na privada).
    [Fact]
    public void Window_RecordsUseOnlyThroughThePolicy()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var calls = Directory.EnumerateFiles(src, "MainWindow*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, i, line)))
            .Where(l => l.line.Contains("MarkUsed(", StringComparison.Ordinal))
            .ToList();
        Assert.Single(calls);
        var file = File.ReadAllText(calls[0].file);
        var method = Regex.Match(file, @"private void RecordVaultUse\([^)]*\)\s*\{(?<body>[^}]*)\}");
        Assert.True(method.Success, "RecordVaultUse em falta");
        Assert.Contains("VaultWindowPolicy.Allows(VaultAction.RecordUse, IsPrivate)", method.Groups["body"].Value);
        Assert.Contains("MarkUsed(", method.Groups["body"].Value);
        // O script do cofre recebe «oferecer» falso na janela privada.
        Assert.Contains("VaultWindowPolicy.Allows(VaultAction.OfferSave, IsPrivate)", File.ReadAllText(Path.Combine(src, "MainWindow.Vault.cs")));
    }
}
