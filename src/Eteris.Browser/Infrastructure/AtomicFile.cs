using System.IO;
using System.Text;

namespace Eteris.Browser.Infrastructure;

// Como correu a leitura de um ficheiro do perfil: em falta (nunca gravado, ou apagado de propósito), lido, lido da cópia
// anterior (.bak) porque o ficheiro estava danificado, ou impossível de ler (danificado sem cópia que sirva, ou preso).
public enum AtomicRead { Missing, Ok, FromBackup, Unreadable }

// Ficheiros de texto do perfil (session.json, settings.json, notas.md…): gravados por um ficheiro temporário descarregado
// para o disco e trocado pelo anterior, que fica ao lado como <ficheiro>.bak (uma geração); o que não se consegue ler
// fica em <ficheiro>.corrupt (uma geração) e volta a cópia anterior. Sem o Flush(flushToDisk), um corte de energia pouco
// depois da troca deixava o ficheiro com o tamanho certo e cheio de zeros. Um ficheiro em falta nunca se repõe a partir
// do .bak. Quem chama apanha IOException e UnauthorizedAccessException.
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void WriteText(string path, string text, bool backup, bool flushToDisk = true)
    {
        // O ReplaceFile do Windows não é consistente com o atributo só de leitura: recusa-se logo, sem tocar em nada.
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"{Path.GetFileName(path)} é só de leitura.");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk);
            }
            if (backup && File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // O ficheiro em falta dá Missing e null (nunca se olha para o .bak). Lido e convertido: Ok. Lido mas danificado
    // (parse devolve null ou lança): copia-se para .corrupt e tenta-se o .bak da mesma maneira (FromBackup); sem .bak que
    // sirva, Unreadable. Uma falha de leitura do próprio ficheiro (preso por outro programa) dá Unreadable sem copiar e
    // sem ir ao .bak: o ficheiro pode estar bom, e a versão anterior passaria por atual.
    public static T? ReadText<T>(string path, Func<string, T?> parse, out AtomicRead outcome) where T : class
    {
        if (!File.Exists(path))
        {
            outcome = AtomicRead.Missing;
            return null;
        }
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            outcome = AtomicRead.Unreadable;
            return null;
        }
        if (Parse(text, parse) is { } value)
        {
            outcome = AtomicRead.Ok;
            return value;
        }
        KeepCopy(path, path + ".corrupt");
        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            try
            {
                if (Parse(File.ReadAllText(backup, Encoding.UTF8), parse) is { } previous)
                {
                    // O ficheiro danificado já está em .corrupt: a versão boa volta ao seu lugar. Sem isto, a 1.ª gravação
                    // seguinte trocava o .bak pelo ficheiro danificado e a cópia boa perdia-se.
                    KeepCopy(backup, path);
                    outcome = AtomicRead.FromBackup;
                    return previous;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        outcome = AtomicRead.Unreadable;
        return null;
    }

    // O que não se leu nunca se grava por cima: antes da 1.ª escrita depois de um Unreadable, o ficheiro antigo vai para
    // .corrupt (se não se deixar mover, lança e a escrita não se faz).
    public static void SetAside(string path)
    {
        if (File.Exists(path)) File.Move(path, path + ".corrupt", overwrite: true);
    }

    public static string? Notice(string fileName, AtomicRead outcome) => outcome switch
    {
        AtomicRead.FromBackup => $"{fileName} estava danificado: voltou a cópia anterior e o ficheiro danificado ficou em {fileName}.corrupt",
        AtomicRead.Unreadable => $"Não foi possível ler {fileName}: o Éteris abriu sem ele e o que lá estava fica guardado em {fileName}.corrupt",
        _ => null,
    };

    private static T? Parse<T>(string text, Func<string, T?> parse) where T : class
    {
        try { return parse(text); }
        catch (Exception) { return null; }
    }

    private static void KeepCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
