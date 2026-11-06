using Eteris.Browser.Data;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData("dark", true, AppTheme.Dark)]
    [InlineData("light", false, AppTheme.Light)]
    [InlineData("system", true, AppTheme.Light)]
    [InlineData("system", false, AppTheme.Dark)]
    [InlineData("nonsense", true, AppTheme.Dark)]
    public void Resolve_FollowsChoiceOrSystem(string choice, bool systemLight, AppTheme expected)
        => Assert.Equal(expected, ThemeResolver.Resolve(choice, systemLight));

    [Fact]
    public void Setting_DefaultsDarkAndValidates()
    {
        Assert.Equal("dark", new UserSettings().Theme);
        Assert.NotNull(new UserSettings().With("theme", "system"));
        Assert.Null(new UserSettings().With("theme", "pink"));
    }

    [Theory]
    [InlineData(AppTheme.Dark, "Theme/Dark.xaml")]
    [InlineData(AppTheme.Light, "Theme/Light.xaml")]
    public void Palette_PathPerTheme(AppTheme theme, string path)
        => Assert.Equal(path, ThemeResolver.PalettePath(theme));
}
