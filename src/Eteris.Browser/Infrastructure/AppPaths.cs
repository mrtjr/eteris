using System.IO;

namespace Eteris.Browser.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eteris");

    public static string Database { get; } = Path.Combine(Root, "eteris.db");

    public static string Profile { get; } = Path.Combine(Root, "profile");

    public static string Filters { get; } = Path.Combine(Root, "filters");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profile);
        Directory.CreateDirectory(Filters);
    }
}
