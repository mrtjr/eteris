using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// B2 (docs/palavras-passe.md): a verificação local — fracas e repetidas —, calculada neste computador, sem nenhum
// pedido de rede e sem mostrar as palavras-passe (só os IDs saem).
public class PasswordHealthTests
{
    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("password")]
    [InlineData("Password1")]
    [InlineData("senha123")]
    [InlineData("qwertyuiop")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("abcdefghij")]
    [InlineData("benfica1904")]
    [InlineData("flamengo")]
    [InlineData("abcd1234")]
    public void Weak(string password) => Assert.True(PasswordHealth.IsWeak(password));

    [Theory]
    [InlineData("Lisboa-Tejo-2024")]
    [InlineData("k7#Vq2!mZp9x")]
    [InlineData("correto cavalo bateria agrafo")]
    [InlineData("Xk7pQ2mZr9TwLb4vNs8e")]
    public void NotWeak(string password) => Assert.False(PasswordHealth.IsWeak(password));

    // Repetidas: a mesma palavra-passe em mais de um item (comparada por hash em memória); fracas: só os IDs.
    [Fact]
    public void Check_ReturnsOnlyIds_WeakAndReusedGroups()
    {
        var report = PasswordHealth.Check([
            ("a", "Xk7pQ2mZr9TwLb4vNs8e"), ("b", "Xk7pQ2mZr9TwLb4vNs8e"), ("c", "123456"), ("d", "k7#Vq2!mZp9x"), ("e", "123456"),
            ("f", "Lisboa-Tejo-2024"),
        ]);
        Assert.Equal(["c", "e"], report.Weak.Order());
        Assert.Equal(2, report.Reused.Count);
        Assert.Contains(report.Reused, g => g.Order().SequenceEqual(["a", "b"]));
        Assert.Contains(report.Reused, g => g.Order().SequenceEqual(["c", "e"]));
        Assert.Equal(6, report.Checked);
        Assert.DoesNotContain("123456", report.ToString());
    }

    // O texto do resumo, em português.
    [Fact]
    public void Summary_InPortuguese()
    {
        Assert.Equal("Nenhuma palavra-passe fraca nem repetida", new HealthReport([], [], 3).Summary);
        Assert.Equal("1 palavra-passe fraca · 2 repetidas", new HealthReport(["a"], [["b", "c"]], 3).Summary);
        Assert.Equal("2 palavras-passe fracas", new HealthReport(["a", "b"], [], 3).Summary);
    }
}
