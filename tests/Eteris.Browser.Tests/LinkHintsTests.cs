using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class LinkHintsTests
{
    [Fact]
    public void FewTargets_GetSingleLetters()
        => Assert.Equal(["a", "s", "d"], LinkHints.Labels(3));

    [Fact]
    public void ManyTargets_GetEqualLengthUniqueLabels()
    {
        var labels = LinkHints.Labels(50);
        Assert.All(labels, l => Assert.Equal(2, l.Length));
        Assert.Equal(50, labels.Distinct().Count());
        // Nenhuma etiqueta é prefixo de outra: escrever a etiqueta inteira escolhe sempre um só alvo.
        Assert.DoesNotContain(labels, a => labels.Any(b => a != b && b.StartsWith(a)));
    }

    [Fact]
    public void Script_IsIsolatedAndCancellable()
    {
        Assert.Contains("attachShadow", LinkHints.Script);
        Assert.Contains("Escape", LinkHints.Script);
        Assert.Contains(LinkHints.Alphabet, LinkHints.Script);
    }
}
