using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class FindFormatTests
{
    [Theory]
    [InlineData("", 0, 0, "")]
    [InlineData("lisboa", 0, 0, "Sem resultados")]
    [InlineData("lisboa", 1, 1, "1 de 1")]
    [InlineData("lisboa", 3, 12, "3 de 12")]
    public void Count_IsPortuguese(string query, int active, int total, string expected)
        => Assert.Equal(expected, FindFormat.Count(query, active, total));
}
