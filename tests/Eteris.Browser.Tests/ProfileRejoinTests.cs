using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Sync;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests;

// DAD-15 e DAD-07 (revisão): quando a base de dados (ou o settings.json) se põe de lado, o perfil novo está vazio (ou com
// predefinições) e a sincronização, comparando-o com a base dela, enviava remoções (ou as predefinições) para a conta e
// para os outros aparelhos. As coleções afetadas ficam marcadas em disco: não se leem como apagadas e, no arranque
// seguinte, voltam a fazer a «primeira junção» (o que só existe aqui sobe; o que a conta tem vale).
public sealed class ProfileRejoinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eteris-rejoin-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "sync-rejoin.txt");

    public ProfileRejoinTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Add_IsPersistent_WithoutDuplicates()
    {
        Assert.Empty(ProfileRejoin.Pending(File_));
        ProfileRejoin.Add(File_, Collections.Bookmarks);
        ProfileRejoin.Add(File_, Collections.Shield);
        ProfileRejoin.Add(File_, Collections.Bookmarks);
        Assert.Equal([Collections.Bookmarks, Collections.Shield], ProfileRejoin.Pending(File_).Order());
        ProfileRejoin.Clear(File_);
        Assert.Empty(ProfileRejoin.Pending(File_));
        Assert.False(File.Exists(File_));
    }

    // Só se volta a juntar o que está ligado neste computador (juntar não liga nada: desligado fica desligado).
    [Fact]
    public void ToRejoin_OnlyWhatIsEnabled()
    {
        var pending = new HashSet<string> { Collections.Bookmarks, Collections.Shield, Collections.Settings };
        var enabled = new HashSet<string> { Collections.Bookmarks, Collections.Settings, Collections.Notes };
        Assert.Equal([Collections.Bookmarks, Collections.Settings], ProfileRejoin.ToRejoin(pending, enabled).Order());
        Assert.Empty(ProfileRejoin.ToRejoin(new HashSet<string>(), enabled));
    }

    [Fact]
    public void Pending_IgnoresBlankAndGarbageLines()
    {
        File.WriteAllText(File_, "\n  bookmarks \n\n");
        Assert.Equal([Collections.Bookmarks], ProfileRejoin.Pending(File_));
    }

    // O que só existe na janela e no serviço fica fixado por texto.
    [Fact]
    public void TheWiring_MarksOnSetAside_SkipsRemovals_AndRejoinsWhenTheAgentStarts()
    {
        var appData = Source("Data/AppData.cs");
        Assert.Contains("ProfileRejoin.Add(Eteris.Sync.Model.Collections.Bookmarks)", appData);
        Assert.Contains("ProfileRejoin.Add(Eteris.Sync.Model.Collections.Shield)", appData);
        var profile = Source("Sync/BrowserProfile.cs");
        Assert.Contains("ProfileRejoin.Add(Collections.Settings)", profile);
        Assert.Contains("ProfileRejoin.Pending()", profile);
        Assert.Contains("ProfileRejoin.Apply(_agent)", Source("Sync/SyncService.cs"));
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
