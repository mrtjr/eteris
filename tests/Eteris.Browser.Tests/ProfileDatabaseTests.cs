using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Data;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests;

// DAD-15: uma base de dados danificada (e só essa) põe-se de lado como eteris.db.bak-<data> e começa uma nova, com aviso;
// uma base só ocupada ou sem permissão volta a tentar-se mais tarde, sem lhe tocar.
public sealed class ProfileDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eteris-bd-" + Guid.NewGuid().ToString("N"));

    public ProfileDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CorruptFile_IsSetAside_AndANewDatabaseOpens()
    {
        var path = Path.Combine(_dir, "eteris.db");
        File.WriteAllText(path, "isto não é uma base de dados SQLite".PadRight(4096, 'x'));
        var error = Assert.ThrowsAny<SqliteException>(() => new BookmarkStore(path));
        Assert.True(ProfileDatabase.IsCorrupt(error));
        var copy = ProfileDatabase.MoveAside(path, new DateTime(2026, 11, 11, 9, 30, 15));
        Assert.Equal(path + ".bak-20261111-093015", copy);
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(path));
        var store = new BookmarkStore(path);
        store.Toggle("https://a.pt/", "A", DateTime.UtcNow);
        Assert.True(store.Contains("https://a.pt/"));
    }

    [Fact]
    public void BusyOrDenied_IsNotCorrupt()
    {
        Assert.True(ProfileDatabase.IsCorrupt(new SqliteException("corrompida", 11)));
        Assert.True(ProfileDatabase.IsCorrupt(new SqliteException("não é uma base", 26)));
        Assert.False(ProfileDatabase.IsCorrupt(new SqliteException("ocupada", 5)));
        Assert.False(ProfileDatabase.IsCorrupt(new SqliteException("não abre", 14)));
        Assert.False(ProfileDatabase.IsCorrupt(new IOException()));
        Assert.Null(ProfileDatabase.MoveAside(Path.Combine(_dir, "nao-existe.db"), DateTime.Now));
    }

    [Fact]
    public void AppData_OpensEveryStoreThroughRetryLazy()
    {
        var appData = Source("Data/AppData.cs");
        // Só o LazyWriter, que não abre a base.
        Assert.Single(Regex.Matches(appData, @"StartupTrace\.Lazy<"));
        Assert.Contains("ProfileDatabase.IsCorrupt(", appData);
        Assert.Contains("a base de dados do perfil não está acessível", Source("MainWindow.SavedSessions.cs"));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
