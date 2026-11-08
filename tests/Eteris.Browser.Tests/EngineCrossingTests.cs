using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Voltar/Avançar entre o motor principal e o motor de vídeo (Tabs/EngineCrossing.cs).
public class EngineCrossingTests
{
    private const string Page = "https://www.w3schools.com/html/html5_video.asp";
    private const string Video = "https://www.w3schools.com/html/mov_bbb.mp4";

    [Fact]
    public void BackTarget_IsThePreviousEntry_WhenTheCurrentPageIsTheOneSwitching()
        => Assert.Equal(Page, EngineCrossing.BackTarget([Page, Video], 1, Video));

    [Fact]
    public void BackTarget_IgnoresTheResumeFragment()
        => Assert.Equal(Page, EngineCrossing.BackTarget([Page, Video], 1, Video + "#t=12"));

    [Fact]
    public void BackTarget_IsTheCurrentEntry_WhenGoingForwardToAnotherPage()
        => Assert.Equal(Page, EngineCrossing.BackTarget([Page], 0, Video));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void BackTarget_NoPreviousEntry_IsNull(int current)
        => Assert.Null(EngineCrossing.BackTarget([Video], current, Video));

    [Fact]
    public void FirstVideoEntry_CanGoBack_AndBackReturnsToTheMainEngine()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(Page);
        Assert.True(crossing.CanGoBack(inVideo: true, engineCanGoBack: false));
        Assert.Equal(EngineCrossing.Step.ToMain, crossing.Back(inVideo: true, engineCanGoBack: false));
        Assert.Equal(Page, crossing.ReturnToMain(Video));
        Assert.Null(crossing.BackUrl);
    }

    [Fact]
    public void LaterVideoEntries_GoBackInsideTheVideoEngine()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(Page);
        Assert.Equal(EngineCrossing.Step.InEngine, crossing.Back(inVideo: true, engineCanGoBack: true));
    }

    [Fact]
    public void WithoutAPreviousPage_BackStaysDisabled()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(null);
        Assert.False(crossing.CanGoBack(inVideo: true, engineCanGoBack: false));
        Assert.Equal(EngineCrossing.Step.None, crossing.Back(inVideo: true, engineCanGoBack: false));
        Assert.Null(crossing.ReturnToMain(Video));
    }

    [Fact]
    public void AfterReturning_ForwardGoesBackToTheVideo_AndBackAgainWorks()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(Page);
        crossing.ReturnToMain(Video);
        Assert.True(crossing.CanGoForward(inVideo: false, engineCanGoForward: false));
        Assert.False(crossing.CanGoBack(inVideo: false, engineCanGoBack: false));
        Assert.Equal(EngineCrossing.Step.ToVideo, crossing.Forward(inVideo: false, engineCanGoForward: false));
        Assert.Equal(Video, crossing.ForwardToVideo());
        // A troca volta a fixar o Voltar a partir do histórico do motor principal (a página atual).
        crossing.EnterVideo(EngineCrossing.BackTarget([Page], 0, Video));
        Assert.True(crossing.CanGoBack(inVideo: true, engineCanGoBack: false));
        Assert.False(crossing.CanGoForward(inVideo: true, engineCanGoForward: false));
    }

    [Fact]
    public void ANewMainEntryAfterReturning_DropsTheForwardToTheVideo()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(Page);
        crossing.ReturnToMain(Video);
        crossing.MainHistoryChanged(canGoBack: false);
        Assert.True(crossing.CanGoForward(inVideo: false, engineCanGoForward: false));
        crossing.MainHistoryChanged(canGoBack: true);
        Assert.False(crossing.CanGoForward(inVideo: false, engineCanGoForward: false));
        Assert.Equal(EngineCrossing.Step.None, crossing.Forward(inVideo: false, engineCanGoForward: false));
    }

    [Fact]
    public void ThePageReturnedTo_StaysInTheMainEngine_UntilAnotherAddress()
    {
        var crossing = new EngineCrossing();
        crossing.EnterVideo(Page);
        Assert.False(crossing.StaysInMain(Page));
        crossing.ReturnToMain(Video);
        Assert.True(crossing.StaysInMain(Page));
        Assert.True(crossing.StaysInMain(Page)); // o motor principal repete o mesmo endereço
        Assert.False(crossing.StaysInMain("https://www.w3schools.com/html/"));
        Assert.False(crossing.StaysInMain(Page));
    }

    [Fact]
    public void MainEngineForward_IsUntouchedWhenItHasItsOwn()
    {
        var crossing = new EngineCrossing();
        Assert.True(crossing.CanGoForward(inVideo: false, engineCanGoForward: true));
        Assert.Equal(EngineCrossing.Step.InEngine, crossing.Forward(inVideo: false, engineCanGoForward: true));
        Assert.Equal(EngineCrossing.Step.None, crossing.Forward(inVideo: true, engineCanGoForward: false));
    }
}
