using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class JumpListTests
{
    [Fact]
    public void Tasks_CoverTheEssentials()
    {
        var tasks = JumpListTasks.All;
        Assert.Equal(["Nova aba", "Nova janela privada", "Histórico", "Definições"], tasks.Select(t => t.Title));
    }

    [Fact]
    public void TaskArguments_AreAcceptedOnStartup()
    {
        foreach (var task in JumpListTasks.All)
        {
            var args = task.Arguments.Split(' ');
            var understood = StartupArgs.InitialUrl(args) is not null || StartupArgs.IsPrivate(args) || StartupArgs.WantsNewTab(args);
            Assert.True(understood, task.Title);
        }
    }

    [Theory]
    [InlineData("--new-tab", true)]
    [InlineData("https://a.pt/", false)]
    public void NewTabFlag_IsRecognised(string arg, bool expected)
        => Assert.Equal(expected, StartupArgs.WantsNewTab([arg]));
}
