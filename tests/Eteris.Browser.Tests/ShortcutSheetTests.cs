using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

public class ShortcutSheetTests
{
    [Fact]
    public void EveryGroupHasShortcuts()
        => Assert.All(ShortcutSheet.Groups, g => Assert.NotEmpty(g.Items));

    [Theory]
    [InlineData("Ctrl+K")]
    [InlineData("F9")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Ctrl+H")]
    public void ListsTheMainShortcuts(string keys)
        => Assert.Contains(ShortcutSheet.Groups.SelectMany(g => g.Items), s => s.Keys.Contains(keys));

    [Fact]
    public void NoDuplicateActions()
    {
        var actions = ShortcutSheet.Groups.SelectMany(g => g.Items).Select(s => s.Keys + s.Action).ToList();
        Assert.Equal(actions.Count, actions.Distinct().Count());
    }
}
