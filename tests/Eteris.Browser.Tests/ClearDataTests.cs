using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ClearDataTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 15, 0, 0);

    [Fact]
    public void Parse_ReadsChoicesAndRange()
    {
        var request = ClearDataRequest.Parse("history,cache", "hour", Now)!;
        Assert.True(request.History);
        Assert.False(request.Cookies);
        Assert.True(request.Cache);
        Assert.Equal(Now.AddHours(-1), request.Since);
    }

    [Fact]
    public void Parse_AllTime()
        => Assert.Equal(DateTime.MinValue, ClearDataRequest.Parse("cookies", "all", Now)!.Since);

    [Theory]
    [InlineData("", "all")]
    [InlineData("passwords", "all")]
    [InlineData("history", "forever")]
    public void Parse_RejectsNothingOrUnknown(string what, string range)
        => Assert.Null(ClearDataRequest.Parse(what, range, Now));

    [Fact]
    public void Summary_IsReadable()
        => Assert.Equal("Histórico, cookies e cache apagados",
            new ClearDataRequest(true, true, true, DateTime.MinValue).Summary);
}
