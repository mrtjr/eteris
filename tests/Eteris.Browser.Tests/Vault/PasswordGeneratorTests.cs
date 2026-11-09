using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C5 (docs/palavras-passe.md): o gerador de palavras-passe. RandomNumberGenerator, 20 carateres por omissão, sem
// carateres que se confundem, com as quatro classes; respeita minlength, maxlength e um subconjunto de pattern.
public class PasswordGeneratorTests
{
    private static readonly string Alphabet = PasswordGenerator.Lower + PasswordGenerator.Upper + PasswordGenerator.Digits + PasswordGenerator.Symbols;

    [Fact]
    public void Default_IsTwentyCharacters_WithEveryClass_AndNothingAmbiguous()
    {
        for (var i = 0; i < 200; i++)
        {
            var pw = PasswordGenerator.Generate()!;
            Assert.Equal(PasswordGenerator.DefaultLength, pw.Length);
            Assert.Contains(pw, c => PasswordGenerator.Lower.Contains(c));
            Assert.Contains(pw, c => PasswordGenerator.Upper.Contains(c));
            Assert.Contains(pw, c => PasswordGenerator.Digits.Contains(c));
            Assert.Contains(pw, c => PasswordGenerator.Symbols.Contains(c));
            Assert.All(pw, c => Assert.Contains(c, Alphabet));
        }
        Assert.All("lIO0o1", c => Assert.DoesNotContain(c, Alphabet));
    }

    // Entropia: 20 carateres de um alfabeto de 68 dão mais de 100 bits.
    [Fact]
    public void Default_HasMoreThan100BitsOfEntropy()
    {
        Assert.Equal(68, Alphabet.Length);
        Assert.True(PasswordGenerator.EntropyBits(PasswordGenerator.DefaultLength, Alphabet.Length) > 100);
    }

    [Fact]
    public void NeverRepeats()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 2000; i++) Assert.True(seen.Add(PasswordGenerator.Generate()!));
    }

    // Distribuição: dentro de cada classe, cada caráter sai perto da média (o sorteio é uniforme).
    [Fact]
    public void Distribution_IsUniformWithinEachClass()
    {
        var counts = new Dictionary<char, int>();
        for (var i = 0; i < 3000; i++)
            foreach (var c in PasswordGenerator.Generate()!) counts[c] = counts.GetValueOrDefault(c) + 1;
        foreach (var set in new[] { PasswordGenerator.Lower, PasswordGenerator.Upper, PasswordGenerator.Digits, PasswordGenerator.Symbols })
        {
            var mean = set.Average(c => counts.GetValueOrDefault(c));
            Assert.All(set, c => Assert.InRange(counts.GetValueOrDefault(c), mean * 0.8, mean * 1.2));
        }
    }

    [Theory]
    [InlineData(null, 16, 16)]
    [InlineData(24, null, 24)]
    [InlineData(8, 12, 12)]
    [InlineData(null, 6, 6)]
    [InlineData(100, null, PasswordGenerator.MaxLength)]
    public void Length_RespectsMinAndMax(int? min, int? max, int expected)
        => Assert.Equal(expected, PasswordGenerator.Generate(new PasswordRules(min, max, null))!.Length);

    [Fact]
    public void ImpossibleLimits_GiveNothing()
        => Assert.Null(PasswordGenerator.Generate(new PasswordRules(20, 10, null)));

    // pattern: o campo valida a palavra-passe inteira (como o HTML: ^(?:pattern)$).
    [Theory]
    [InlineData("[A-Za-z0-9]+", 20)]
    [InlineData("[a-z0-9]{8,12}", 12)]
    [InlineData(@"\d{6}", 6)]
    [InlineData("[A-Z]{4}[0-9]{4}", 8)]
    [InlineData(@"(?=.*\d)(?=.*[a-z])(?=.*[A-Z]).{10,}", 20)]
    public void Pattern_IsRespected(string pattern, int length)
    {
        for (var i = 0; i < 30; i++)
        {
            var pw = PasswordGenerator.Generate(new PasswordRules(null, null, pattern));
            Assert.NotNull(pw);
            Assert.Matches("^(?:" + pattern + ")$", pw);
            Assert.Equal(length, pw!.Length);
        }
    }

    [Theory]
    [InlineData("abc")]          // só um texto fixo
    [InlineData("[")]            // padrão inválido
    public void Pattern_ThatCannotBeMet_GivesNothing(string pattern)
        => Assert.Null(PasswordGenerator.Generate(new PasswordRules(null, null, pattern)));

    // ---- Quando se oferece e o que a lista mostra ----

    [Theory]
    [InlineData("new-password", true, false, true)]
    [InlineData("password", true, false, false)]   // a atual é das sugestões (C3)
    [InlineData("new-password", false, false, false)] // «Oferecer para guardar palavras-passe» desligado
    [InlineData("new-password", true, true, false)]   // janela privada (D-3)
    public void Generator_IsOffered_OnlyOnNewPasswords_OutsidePrivate(string kind, bool offer, bool isPrivate, bool expected)
        => Assert.Equal(expected, GeneratorPolicy.Offers(kind, offer, isPrivate));

    // A pré-visualização é parcial: os primeiros carateres e reticências.
    [Fact]
    public void Row_ShowsOnlyAPartialPreview()
    {
        var row = GeneratorPolicy.Row("Xk7aQ9-zP2mN4tR8vW3y");
        Assert.Equal("Usar palavra-passe forte", row.Title);
        Assert.Equal("Xk7a…", row.Detail);
        Assert.DoesNotContain("Q9-zP2", row.ToString());
    }

    // «Gerar palavra-passe forte» no menu de contexto: só num campo de palavra-passe e fora da janela privada.
    [Fact]
    public void ContextMenu_OffersTheGenerator_OnPasswordFields()
    {
        static PageContext Ctx(bool isPrivate, VaultFieldInfo? field)
            => new(null, null, "", IsEditable: true, false, false, isPrivate) { Vault = field };
        bool Has(PageContext c) => PageMenu.Build(c, "DuckDuckGo").Any(i => i?.Command == PageCommand.GeneratePassword);
        Assert.True(Has(Ctx(false, new VaultFieldInfo(IsPassword: true, CanGenerate: true))));
        Assert.False(Has(Ctx(false, new VaultFieldInfo(IsPassword: false, CanGenerate: true))));
        Assert.False(Has(Ctx(false, new VaultFieldInfo(IsPassword: true, CanGenerate: false))));
        Assert.False(Has(Ctx(false, null)));
        Assert.Equal("Gerar palavra-passe forte", PageMenu.Build(Ctx(false, new VaultFieldInfo(true, true)), "x").First(i => i?.Command == PageCommand.GeneratePassword)!.Label);
    }

    // ---- Guardada antes do envio, «Não confirmada» até ao sucesso ----

    [Fact]
    public void Generated_IsSavedBeforeTheSubmit_AsUnconfirmed_AndTheSuccessConfirmsIt()
    {
        using var f = new VaultFixture();
        var id = f.Vault.SaveGenerated("https://conta.exemplo.test/registo", "Gerada-Xk7aQ9");
        var saved = f.Vault.GetLogin(id)!;
        Assert.True(saved.Generated);
        Assert.Equal("", saved.Username);
        // Outra gerada no mesmo site antes do envio substitui a primeira (a anterior fica no histórico).
        Assert.Equal(id, f.Vault.SaveGenerated("https://conta.exemplo.test/", "Gerada-2"));
        Assert.Equal("Gerada-Xk7aQ9", f.Vault.PreviousPassword(id));
        var confirmed = f.Vault.ConfirmGenerated(id, "ana@exemplo.test");
        var login = f.Vault.GetLogin(confirmed)!;
        Assert.False(login.Generated);
        Assert.Equal("ana@exemplo.test", login.Username);
        Assert.Equal("Gerada-2", login.Password);
    }

    // Uma gerada para uma conta que já existe (mudança de palavra-passe): nunca substitui a confirmada antes do envio; ao
    // confirmar, a conta fica com a nova e a antiga vai para o histórico.
    [Fact]
    public void Generated_ForAnExistingAccount_ReplacesItOnlyWhenConfirmed()
    {
        using var f = new VaultFixture();
        var old = f.Vault.SaveLogin(new LoginItem("", "https://conta.exemplo.test", "ana@exemplo.test", "Antiga-0!", null, default, default, null, null, false, null));
        var id = f.Vault.SaveGenerated("https://conta.exemplo.test", "Gerada-3", "ana@exemplo.test");
        Assert.NotEqual(old, id);
        Assert.Equal("Antiga-0!", f.Vault.GetLogin(old)!.Password);
        Assert.Equal(old, f.Vault.ConfirmGenerated(id, "ana@exemplo.test"));
        Assert.Equal("Gerada-3", f.Vault.GetLogin(old)!.Password);
        Assert.Equal("Antiga-0!", f.Vault.PreviousPassword(old));
        Assert.Null(f.Vault.GetLogin(id));
    }
}
