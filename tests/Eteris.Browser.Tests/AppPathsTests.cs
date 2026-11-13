using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class AppPathsTests
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void DefaultRoot_IsUnderLocalApplicationData_NamedEteris()
        => Assert.Equal(Path.Combine(LocalAppData, "Eteris"), AppPaths.DefaultRoot);

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
    public void Normalize_DropsTheTrailingSeparator()
    {
        Assert.Equal(@"C:\dados\perfil", AppPaths.Normalize(@"C:\dados\perfil\"));
        Assert.Equal(@"C:\dados\perfil", AppPaths.Normalize(@"C:\dados\perfil"));
        Assert.Equal(@"C:\", AppPaths.Normalize(@"C:\"));
    }

    // Um --user-data-dir com o nome curto 8.3 (UTILIZ~1) de uma pasta do caminho é a mesma pasta do perfil verdadeiro: a
    // porta «só num perfil de teste» (IsCustomProfile) não pode deixá-lo passar por outro perfil.
    [Fact]
    public void LongForm_ExpandsAShortName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eteris perfil comprido " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var buffer = new StringBuilder(1024);
            var n = GetShortPathNameW(dir, buffer, buffer.Capacity);
            var shortName = n > 0 && n < buffer.Capacity ? buffer.ToString() : dir;
            // Um volume sem nomes curtos 8.3: não há nada a expandir.
            if (!string.Equals(shortName, dir, StringComparison.OrdinalIgnoreCase))
                Assert.Equal(dir, AppPaths.LongForm(shortName), ignoreCase: true);
            Assert.Equal(@"C:\nao\existe\x", AppPaths.LongForm(@"C:\nao\existe\x"));   // o que não existe fica como veio
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UseRoot_NormalizesAndExpands()
    {
        var code = Sync.GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "AppPaths.cs");
        Assert.Contains("Root = LongForm(Normalize(root));", code);
        Assert.Contains("Normalize(DefaultRoot)", code);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetShortPathNameW(string path, StringBuilder buffer, int length);
}
