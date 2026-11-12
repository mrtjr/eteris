using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// DAD-07: os ficheiros do perfil (sessão, definições, notas) gravam-se descarregados para o disco e com a versão anterior
// ao lado (.bak); o que não se consegue ler fica em .corrupt e volta a versão anterior, com aviso.
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eteris-atomic-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public AtomicFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "ficheiro.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Write_KeepsThePreviousVersionAsBak()
    {
        AtomicFile.WriteText(_path, "um", backup: true);
        Assert.False(File.Exists(_path + ".bak"));
        AtomicFile.WriteText(_path, "dois", backup: true);
        Assert.Equal("dois", File.ReadAllText(_path));
        Assert.Equal("um", File.ReadAllText(_path + ".bak"));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Write_WithoutBackup_LeavesNoBak()
    {
        AtomicFile.WriteText(_path, "um", backup: false);
        AtomicFile.WriteText(_path, "dois", backup: false);
        Assert.Equal("dois", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Fact]
    public void Read_Corrupt_FallsBackToBak_AndKeepsTheCorruptCopy()
    {
        AtomicFile.WriteText(_path, "um", backup: true);
        AtomicFile.WriteText(_path, "dois", backup: true);
        File.WriteAllBytes(_path, new byte[16]);
        Assert.Equal("um", AtomicFile.ReadText(_path, text => text.Contains('\0') ? null : text, out var outcome));
        Assert.Equal(AtomicRead.FromBackup, outcome);
        Assert.Equal(new byte[16], File.ReadAllBytes(_path + ".corrupt"));
        // A versão boa volta ao lugar: a gravação seguinte não põe o ficheiro danificado no .bak.
        Assert.Equal("um", File.ReadAllText(_path));
        AtomicFile.WriteText(_path, "tres", backup: true);
        Assert.Equal("um", File.ReadAllText(_path + ".bak"));
    }

    [Fact]
    public void Read_OutcomesWithoutBackup()
    {
        Assert.Null(AtomicFile.ReadText(_path, text => text, out var missing));
        Assert.Equal(AtomicRead.Missing, missing);
        File.WriteAllText(_path, "lixo");
        Assert.Null(AtomicFile.ReadText<string>(_path, _ => throw new FormatException(), out var unreadable));
        Assert.Equal(AtomicRead.Unreadable, unreadable);
        Assert.Equal("lixo", AtomicFile.ReadText(_path, text => text, out var ok));
        Assert.Equal(AtomicRead.Ok, ok);
    }

    // Um ficheiro em falta nunca é reposto a partir do .bak: foi apagado de propósito (ou nunca existiu).
    [Fact]
    public void Read_MissingFile_IgnoresTheBak()
    {
        AtomicFile.WriteText(_path, "um", backup: true);
        AtomicFile.WriteText(_path, "dois", backup: true);
        File.Delete(_path);
        Assert.Null(AtomicFile.ReadText(_path, text => text, out var outcome));
        Assert.Equal(AtomicRead.Missing, outcome);
    }

    // Preso por outro programa: o ficheiro pode estar bom e a versão anterior passaria por atual, por isso não se vai ao .bak.
    [Fact]
    public void Read_LockedFile_IsUnreadable_WithoutTouchingTheBak()
    {
        AtomicFile.WriteText(_path, "um", backup: true);
        AtomicFile.WriteText(_path, "dois", backup: true);
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(AtomicFile.ReadText(_path, text => text, out var outcome));
        Assert.False(File.Exists(_path + ".corrupt"));
        Assert.Equal("dois", AtomicFile.ReadText(_path, text => text, out var again));
        Assert.Equal(AtomicRead.Ok, again);
    }

    [Fact]
    public void Write_OnAReadOnlyFile_Throws()
    {
        AtomicFile.WriteText(_path, "um", backup: true);
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try { Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.WriteText(_path, "dois", backup: true)); }
        finally { File.SetAttributes(_path, FileAttributes.Normal); }
        Assert.Equal("um", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Notice_OnlyWhenTheFileWasDamaged()
    {
        Assert.Null(AtomicFile.Notice("settings.json", AtomicRead.Ok));
        Assert.Null(AtomicFile.Notice("settings.json", AtomicRead.Missing));
        Assert.Contains("settings.json.corrupt", AtomicFile.Notice("settings.json", AtomicRead.FromBackup));
        Assert.Contains("settings.json.corrupt", AtomicFile.Notice("settings.json", AtomicRead.Unreadable));
    }

    // O que só existe na janela fica fixado por texto: o temporizador grava fora do fio da janela, a saída grava já, uma
    // falha avisa-se pelo Dispatcher sem bloquear, e os ficheiros danificados avisam-se uma vez no arranque.
    [Fact]
    public void Window_PostsTheTimerSave_WritesNowOnExit_AndWarnsAboutDamagedFiles()
    {
        var window = Source("MainWindow.Session.cs");
        Assert.Contains("LatestWriter<IReadOnlyList<Data.SessionWindow>>", window);
        Assert.Contains("s_sessionWriter.Post(snapshot)", window);
        Assert.Contains("s_sessionWriter.WriteNow(snapshot)", window);
        Assert.Contains("SaveSessionNow(now: true)", window);
        Assert.Contains("Dispatcher.InvokeAsync(WarnSessionNotSaved)", window);
        Assert.DoesNotContain("Dispatcher.Invoke(", window);
        Assert.Contains("AtomicFile.Notice(\"settings.json\", Data.Settings.LoadOutcome)", window);
        Assert.Contains("AtomicFile.Notice(\"session.json\", SessionFile.LastLoad)", window);
        Assert.Contains("if (Data.Settings.Unreadable) unreadable.Add(Collections.Settings);", Source("Sync/BrowserProfile.cs"));
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
