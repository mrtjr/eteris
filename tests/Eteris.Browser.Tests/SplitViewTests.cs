using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class SplitViewTests
{
    private readonly BrowserTab _a = new("https://a.pt/"), _b = new("https://b.pt/"), _c = new("https://c.pt/");

    [Fact]
    public void Panes_AreLeftAndRight()
    {
        var split = new SplitView(_a, _b);
        Assert.Equal(SplitPane.Left, split.PaneOf(_a));
        Assert.Equal(SplitPane.Right, split.PaneOf(_b));
        Assert.Null(split.PaneOf(_c));
    }

    [Fact]
    public void SwitchingBetweenPanes_KeepsSplit()
        => Assert.True(new SplitView(_a, _b).SurvivesActivation(_b));

    [Fact]
    public void ActivatingAnotherTab_EndsSplit()
        => Assert.False(new SplitView(_a, _b).SurvivesActivation(_c));

    [Fact]
    public void ClosingAMember_EndsSplit()
    {
        var split = new SplitView(_a, _b);
        Assert.False(split.SurvivesRemoval(_b));
        Assert.True(split.SurvivesRemoval(_c));
    }

    [Fact]
    public void CannotSplitTabWithItself()
        => Assert.Throws<ArgumentException>(() => new SplitView(_a, _a));

    [Fact]
    public void Partner_IsTheOtherPane()
    {
        var split = new SplitView(_a, _b);
        Assert.Same(_b, split.PartnerOf(_a));
        Assert.Same(_a, split.PartnerOf(_b));
    }

    [Fact]
    public void Swap_ExchangesThePanesWithoutLosingTheTabs()
    {
        var split = new SplitView(_a, _b);
        split.Swap();
        Assert.Same(_b, split.Left);
        Assert.Same(_a, split.Right);
        Assert.Equal(SplitPane.Right, split.PaneOf(_a));
        Assert.True(split.SurvivesActivation(_a));
    }

    [Fact]
    public void Orientation_DefaultsToSideBySideAndToggles()
    {
        var split = new SplitView(_a, _b);
        Assert.False(split.IsStacked);
        split.ToggleOrientation();
        Assert.Equal(SplitOrientation.Stacked, split.Orientation);
        split.ToggleOrientation();
        Assert.Equal(SplitOrientation.SideBySide, split.Orientation);
        Assert.True(new SplitView(_a, _b, SplitOrientation.Stacked).IsStacked);
    }

    [Theory]
    [InlineData("stacked", SplitOrientation.Stacked)]
    [InlineData("STACKED", SplitOrientation.Stacked)]
    [InlineData("side", SplitOrientation.SideBySide)]
    [InlineData("", SplitOrientation.SideBySide)]
    [InlineData(null, SplitOrientation.SideBySide)]
    [InlineData("lixo", SplitOrientation.SideBySide)]
    public void OrientationName_RoundTripsAndTolerateGarbage(string? name, SplitOrientation expected)
    {
        Assert.Equal(expected, SplitView.ParseOrientation(name));
        Assert.Equal(expected, SplitView.ParseOrientation(SplitView.Name(expected)));
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.05, 0.2)]
    [InlineData(0.97, 0.8)]
    public void Ratio_IsClamped(double requested, double expected)
        => Assert.Equal(expected, SplitView.ClampRatio(requested), 3);
}
