using System.IO;

namespace Eteris.Browser.Downloads;

// O que o painel de transferências faz antes de abrir um ficheiro: um clique tem de ser deliberado (1 s de guarda depois
// de o painel abrir sozinho ou de a transferência acabar: a linha pode aparecer debaixo de um cursor parado) e um tipo
// que executa pede confirmação, com o nome completo e a origem à vista.
public static class DownloadRisk
{
    public static readonly TimeSpan ClickGuard = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller",
        ".application", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar",
        ".lnk", ".url", ".reg", ".msc", ".scf", ".inf", ".chm", ".dll", ".gadget", ".diagcab", ".iso", ".img", ".vhd", ".vhdx",
        ".appref-ms", ".settingcontent-ms", ".msp", ".ws", ".wsc", ".sct", ".xll",
    };

    // O Windows ignora espaços e pontos no fim do nome («a.exe. » é «a.exe»): a extensão lê-se depois de os tirar.
    public static bool Executes(string fileName)
    {
        var name = DownloadFormat.StripInvisible(fileName).TrimEnd(' ', '.');
        return ExecutableExtensions.Contains(Path.GetExtension(name));
    }

    public static bool AcceptsClick(DateTime nowUtc, DateTime? openedByItselfUtc, DateTime? completedUtc)
        => !(openedByItselfUtc is { } opened && nowUtc - opened < ClickGuard)
           && !(completedUtc is { } done && nowUtc - done < ClickGuard);

    // O nome mostra-se sem os caracteres que invertem a leitura, como na linha da transferência (DownloadFormat.SplitName).
    public static string Question(string fileName, string? source)
        => $"«{DownloadFormat.StripInvisible(fileName)}» é um programa ou um script: ao abrir, corre neste computador com os seus acessos."
           + $"\n\nOrigem: {(string.IsNullOrWhiteSpace(source) ? "desconhecida" : source)}"
           + "\n\nAbrir mesmo assim?";
}
