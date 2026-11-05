using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class AppPathsTests
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void Root_IsUnderLocalApplicationData_NamedEteris()
        => Assert.Equal(Path.Combine(LocalAppData, "Eteris"), AppPaths.Root);

    [Fact]
    public void Database_IsEterisDbInsideRoot()
        => Assert.Equal(Path.Combine(AppPaths.Root, "eteris.db"), AppPaths.Database);

    [Fact]
    public void Profile_IsProfileInsideRoot()
        => Assert.Equal(Path.Combine(AppPaths.Root, "profile"), AppPaths.Profile);

    [Fact]
    public void Filters_IsFiltersInsideRoot()
        => Assert.Equal(Path.Combine(AppPaths.Root, "filters"), AppPaths.Filters);

    [Fact]
    public void Root_PreservesNonAsciiUserName()
        => Assert.Contains(Environment.UserName, AppPaths.Root);
}
