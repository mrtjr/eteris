using System.IO;
using System.Net;
using System.Text;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

internal enum KitFolderKind { Local, Cloud, Unknown }

// Para onde vai o ficheiro do kit: uma pasta local conhecida, uma pasta da nuvem (Provider: «OneDrive», «Google Drive»,
// «Dropbox») ou uma que o Éteris não consegue confirmar.
internal sealed record KitFolder(KitFolderKind Kind, string? Provider = null);

internal enum DriveKind { Fixed, Removable, Network, Other }

internal enum KitSaveResult { Saved, Cancelled, NeedsConfirm, Failed }

// O kit de recuperação como documento (0.2.15): uma página HTML autónoma, sem scripts nem pedidos, legível impressa a
// preto e branco, com as 24 palavras, a impressão digital, a data e os avisos; e para onde se pode gravar sem aviso.
// O e-mail e o nome da conta nunca entram (a linha «Conta Google» preenche-se à mão).
internal static class RecoveryKit
{
    public const int MaxFileBytes = 1 << 20;

    public const string UnknownFolderWarning = "O Éteris não consegue confirmar que esta pasta fica só neste computador. Se for copiada para a nuvem, quem tiver essa conta fica com o kit.";
    public const string KeepOffThisComputer = "Não deixe este ficheiro neste computador: passe-o para uma pen ou imprima-o e apague-o daqui.";
    // A 2.ª frase do conselho da conta do Windows sem proteção e a frase junto de «Imprimir»: só na página.
    public const string UnprotectedNote = "Qualquer pessoa com acesso a este computador pode ver o kit.";
    public const string PrintNote = "«Imprimir para PDF» cria um ficheiro com o kit: trate-o como o ficheiro do kit. Numa impressora partilhada, vá buscar a folha logo.";

    public const string AloneOpensNothing = "O kit sozinho não abre nada: também é preciso entrar na sua conta Google. E a Google sozinha também não.";
    public const string NoPhotos = "Não fotografe com o telemóvel se as fotos forem para a nuvem, e não guarde no Google Drive: se ficar na mesma conta, a Google passa a ter a chave.";

    private const string Title = "Kit de recuperação do Éteris";
    private const string WhatItOpens = "Este kit abre a cópia cifrada do seu Éteris na nuvem: favoritos, palavras-passe e definições.";
    private const string HowToUse = "Num computador novo, entre no Éteris com a sua conta Google e escolha «Colar o kit» ou «Abrir o ficheiro do kit…». Guarde este kit fora do computador; guarde-o como guardaria uma chave de casa.";

    private static readonly string[] Months =
        ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

    // «Kit de recuperação do Éteris 2026-11-09.html»
    public static string FileName(DateTime localDate) => $"{Title} {localDate:yyyy'-'MM'-'dd}.html";

    // «9 de novembro de 2026» (os meses à mão: o mesmo texto em qualquer cultura do Windows).
    private static string LongDate(DateTime localDate) => $"{localDate.Day} de {Months[localDate.Month - 1]} de {localDate.Year}";

    public static string Html(string words, string kitId, DateTime localDate)
    {
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var html = new StringBuilder();
        // translate="no" e notranslate: aberto num navegador com tradução automática, as palavras não vão para o tradutor.
        html.Append("<!doctype html>\n<html lang=\"pt-PT\" translate=\"no\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"google\" content=\"notranslate\">\n<title>").Append(Title).Append("</title>\n");
        // Sem nenhuma regra «@» (nem media, nem page, nem import) e sem cores que dependam de impressora a cores.
        html.Append("<style>\n")
            .Append("body { font-family: Segoe UI, Arial, sans-serif; color: #000; background: #fff; margin: 32px; max-width: 760px; font-size: 14px; line-height: 1.5; }\n")
            .Append("h1 { font-size: 22px; margin: 0 0 4px; }\n")
            .Append(".id { margin: 0 0 20px; }\n")
            .Append("table { border-collapse: collapse; width: 100%; margin: 0 0 20px; }\n")
            .Append("td { border: 1px solid #000; padding: 8px 10px; font-size: 16px; width: 25%; }\n")
            .Append("td span { display: inline-block; min-width: 28px; font-size: 12px; }\n")
            .Append(".line { font-family: Consolas, monospace; font-size: 16px; border: 1px solid #000; padding: 10px; word-break: break-all; }\n")
            .Append(".warn { border-left: 4px solid #000; padding-left: 10px; }\n")
            .Append(".account { margin-top: 28px; font-size: 16px; }\n")
            .Append("</style>\n</head>\n");
        html.Append("<body ").Append(RecoveryCode.KitAttribute).Append("=\"").Append(WebUtility.HtmlEncode(words)).Append("\">\n");
        html.Append("<h1>").Append(Title).Append("</h1>\n");
        html.Append("<p class=\"id\">Gerado em ").Append(WebUtility.HtmlEncode(LongDate(localDate)))
            .Append(" · termina em <b>").Append(WebUtility.HtmlEncode(kitId)).Append("</b></p>\n");
        if (tokens.Length == RecoveryCode.WordCount)
        {
            html.Append("<table>\n");
            for (var row = 0; row < tokens.Length / 4; row++)
            {
                html.Append("<tr>");
                for (var col = 0; col < 4; col++)
                {
                    var i = row * 4 + col;
                    html.Append("<td><span>").Append(i + 1).Append(".</span> ").Append(WebUtility.HtmlEncode(tokens[i])).Append("</td>");
                }
                html.Append("</tr>\n");
            }
            html.Append("</table>\n");
        }
        else html.Append("<p class=\"line\">").Append(WebUtility.HtmlEncode(words)).Append("</p>\n");
        html.Append("<p>").Append(WhatItOpens).Append("</p>\n");
        html.Append("<p>").Append(HowToUse).Append("</p>\n");
        html.Append("<p>").Append(AloneOpensNothing).Append("</p>\n");
        html.Append("<p class=\"warn\">").Append(NoPhotos).Append("</p>\n");
        html.Append("<p class=\"warn\">").Append(KeepOffThisComputer).Append("</p>\n");
        html.Append("<p class=\"account\">Conta Google: ________________________________________</p>\n");
        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    // Por esta ordem: um caminho de rede não se confirma; uma pasta da nuvem conhecida (a nuvem ganha a uma pasta
    // conhecida redirecionada para lá); uma pen é local; num disco fixo, só as pastas locais conhecidas (Transferências,
    // Documentos, Ambiente de trabalho) passam sem aviso. Melhor esforço: por isso os avisos fixos vão sempre no kit.
    public static KitFolder Folder(string path, Func<string, string?> env, IReadOnlyList<string> localRoots, Func<string, DriveKind> driveOf)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return new(KitFolderKind.Unknown);
        if (Vault.PasswordCsv.CloudFolder(full, env) is { } provider) return new(KitFolderKind.Cloud, provider);
        return driveOf(Path.GetPathRoot(full)!) switch
        {
            DriveKind.Removable => new(KitFolderKind.Local),
            DriveKind.Fixed when localRoots.Any(root => Inside(full, root)) => new(KitFolderKind.Local),
            _ => new(KitFolderKind.Unknown),
        };
    }

    // «C:\X\» contém «C:\X\f», nunca «C:\Xy\f».
    private static bool Inside(string full, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string FolderWarning(KitFolder folder) => folder.Kind switch
    {
        KitFolderKind.Cloud => $"Esta pasta vai para a nuvem de {folder.Provider}. Quem tiver essa conta fica com o kit.",
        KitFolderKind.Unknown => UnknownFolderWarning,
        _ => string.Empty,
    };

    // Grava o kit só onde o utilizador escolheu: fora de uma pasta local conhecida, só depois de «Guardar mesmo assim».
    // Sem pastas temporárias, cópias de segurança nem registos com o caminho ou as palavras.
    public static KitSaveResult Save(RecoveryKitSecret kit, string? path, bool accepted, KitFolder folder, Action<string, string> write, DateTime localDate)
    {
        if (path is null) return KitSaveResult.Cancelled;
        if (folder.Kind != KitFolderKind.Local && !accepted) return KitSaveResult.NeedsConfirm;
        try
        {
            write(path, Html(kit.Words, kit.KitId, localDate));
            return KitSaveResult.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return KitSaveResult.Failed;
        }
    }

    // As pastas locais conhecidas e o tipo de disco, na máquina real.
    public static KitFolder FolderOnThisComputer(string path) => Folder(path, Environment.GetEnvironmentVariable,
        [Downloads.DownloadPaths.DownloadsFolder(), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)],
        DriveOf);

    private static DriveKind DriveOf(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType switch
            {
                DriveType.Fixed => DriveKind.Fixed,
                DriveType.Removable => DriveKind.Removable,
                DriveType.Network => DriveKind.Network,
                _ => DriveKind.Other,
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DriveKind.Other;
        }
    }

    // ---- o ecrã de entrada ------------------------------------------------------------------------------------------

    // A palavra que o campo do kit pode completar: só a partir das 4 primeiras letras (dobradas: sem acentos nem
    // maiúsculas), nunca com mais de 16 caracteres; no máximo 2 (a página só sugere quando há uma).
    public static IReadOnlyList<string> Completion(string? typed)
    {
        if (typed is null || typed.Length > 16) return [];
        var letters = typed.Normalize(NormalizationForm.FormD).Count(char.IsLetter);
        if (letters < 4) return [];
        return RecoveryCode.Complete(typed, 2);
    }

    // «Abrir o ficheiro do kit…»: o segredo do ficheiro que o Éteris gerou (no máximo 1 MiB); um ficheiro que não é um
    // kit, ou que não se lê, dá false.
    public static bool TryReadFile(string path, Func<string, long> sizeOf, Func<string, string> readText, out byte[] secret)
    {
        secret = [];
        try
        {
            if (sizeOf(path) > MaxFileBytes) return false;
            return RecoveryCode.TryReadKit(readText(path), out secret);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A gravação real: UTF-8 sem BOM, no caminho escolhido, descarregada para o disco antes de o kit contar como
    // guardado (uma falha de energia logo a seguir não deixa um ficheiro vazio com o lembrete já calado).
    public static void WriteFile(string path, string html)
    {
        var bytes = new UTF8Encoding(false).GetBytes(html);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
