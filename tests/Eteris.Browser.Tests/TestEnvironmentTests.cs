using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O ambiente em que a bateria corre (TestEnvironment): nunca o perfil de quem a corre e nunca as variáveis ETERIS_* da
// consola dele.
public class TestEnvironmentTests
{
    [Fact]
    public void Profile_IsATempFolder_NeverTheRealOne()
    {
        Assert.True(AppPaths.IsCustomProfile);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), AppPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AppPaths.DefaultRoot, AppPaths.Root);
    }

    [Fact]
    public void Process_HasNoEterisVariables()
        => Assert.DoesNotContain(Environment.GetEnvironmentVariables().Keys.Cast<string>(),
                                 k => k.StartsWith("ETERIS_", StringComparison.OrdinalIgnoreCase));
}
