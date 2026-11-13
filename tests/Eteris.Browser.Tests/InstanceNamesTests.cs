using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// INF-06: o nome do canal entre instâncias segue a pasta do perfil (normalizada) e a sessão do Windows.
public class InstanceNamesTests
{
    [Fact]
    public void SameFolder_WithOrWithoutTrailingSlashOrCase_GivesTheSameNames()
    {
        Assert.Equal(InstanceNames.Hash(@"C:\dados\perfil"), InstanceNames.Hash(@"c:\DADOS\Perfil\"));
        Assert.Equal(InstanceNames.Mutex(@"C:\dados\perfil", true), InstanceNames.Mutex(@"C:\dados\perfil\", true));
        Assert.Equal(InstanceNames.Channel(@"C:\dados\perfil", 1), InstanceNames.Channel(@"c:\dados\PERFIL\", 1));
    }

    [Fact]
    public void DefaultProfilesOfTwoUsers_GiveDifferentChannels()
        => Assert.NotEqual(InstanceNames.Channel(@"C:\dados\ana\Eteris", 1), InstanceNames.Channel(@"C:\dados\rui\Eteris", 1));

    [Fact]
    public void Channel_HasTheWindowsSession()
    {
        Assert.EndsWith(".s2", InstanceNames.Channel(@"C:\dados\perfil", 2));
        Assert.NotEqual(InstanceNames.Channel(@"C:\dados\perfil", 1), InstanceNames.Channel(@"C:\dados\perfil", 2));
    }

    [Fact]
    public void Mutex_DefaultProfile_KeepsTheOldName()
    {
        Assert.Equal("Eteris.Browser", InstanceNames.Mutex(@"C:\dados\ana\Eteris", customProfile: false));
        Assert.Equal("Eteris.Browser." + InstanceNames.Hash(@"C:\dados\perfil"), InstanceNames.Mutex(@"C:\dados\perfil", customProfile: true));
        Assert.Matches("^[0-9A-F]{12}$", InstanceNames.Hash(@"C:\dados\perfil"));
    }

    [Fact]
    public void Program_UsesTheNames()
    {
        var main = Sync.GoogleSyncTests.Src("Eteris.Browser", "Program.cs");
        Assert.Contains("InstanceNames.Mutex(AppPaths.Root, AppPaths.IsCustomProfile)", main);
        Assert.Contains("InstanceNames.Channel(AppPaths.Root,", main);
        Assert.Contains("InstanceNames.Base", main);
    }
}
