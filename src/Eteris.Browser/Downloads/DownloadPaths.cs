using System.IO;
using System.Runtime.InteropServices;

namespace Eteris.Browser.Downloads;

public static class DownloadPaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder()
    {
        if (SHGetKnownFolderPath(DownloadsFolderId, 0, 0, out var path) == 0) return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // O nome sugerido vem do site: nunca pode sair da pasta nem conter caracteres inválidos.
    public static string Unique(string directory, string suggestedName)
    {
        var name = Path.GetFileName(suggestedName.Replace('/', '\\'));
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim(' ', '.');
        if (name.Length == 0) name = "transferência";

        var candidate = Path.Combine(directory, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; File.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
        return candidate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out string path);
}
