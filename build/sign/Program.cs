using System.Security.Cryptography;
using System.Text;
using Eteris.Browser.Updates;

namespace Eteris.Sign;

// dotnet run --project build/sign -c Release -- <comando>
//   new-key <ficheiro.pem>                      gera a chave (pede a frase-passe duas vezes) e escreve só a linha pública
//   public <ficheiro.pem>                       escreve a linha pública de uma chave existente
//   sign <ficheiro.pem> <ficheiro>              escreve <ficheiro>.sig
//   verify <update-keys.txt> <ficheiro> <ficheiro.sig>
// Saída: 0 feito ou válido, 1 recusado, 2 uso errado. A frase-passe lê-se da consola sem eco, ou de uma linha da entrada
// quando esta vem redirecionada; nunca de argumentos nem do ambiente (ficariam no histórico ou no registo).
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["new-key", var pem] => NewKey(pem),
                ["public", var pem] => Public(pem),
                ["sign", var pem, var file] => SignFile(pem, file),
                ["verify", var keys, var file, var sig] => VerifyFile(keys, file, sig),
                _ => Usage(),
            };
        }
        catch (CryptographicException)
        {
            Console.Error.WriteLine("recusado: frase-passe errada ou chave ilegível");
            return 1;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine("recusado: " + e.Message);
            return 1;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine("recusado: " + e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("uso: new-key <ficheiro.pem> | public <ficheiro.pem> | sign <ficheiro.pem> <ficheiro> | verify <update-keys.txt> <ficheiro> <ficheiro.sig>");
        return 2;
    }

    private static int NewKey(string pemFile)
    {
        if (File.Exists(pemFile))
        {
            Console.Error.WriteLine($"recusado: {pemFile} já existe (uma chave nunca se escreve por cima)");
            return 1;
        }
        var password = ReadPassword("Frase-passe da chave nova: ");
        if (!Console.IsInputRedirected && ReadPassword("Outra vez: ") != password)
        {
            Console.Error.WriteLine("recusado: as duas frases-passe não são iguais");
            return 1;
        }
        if (password.Length < 12)
        {
            Console.Error.WriteLine("recusado: a frase-passe tem de ter pelo menos 12 carateres");
            return 1;
        }
        var pem = Signer.NewKeyPem(password);
        using (var stream = new FileStream(pemFile, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(pem);
        Console.WriteLine(Signer.PublicKey(pem, password));
        return 0;
    }

    private static int Public(string pemFile)
    {
        var pem = File.ReadAllText(pemFile);
        Console.WriteLine(Signer.PublicKey(pem, ReadPassword("Frase-passe: ")));
        return 0;
    }

    private static int SignFile(string pemFile, string file)
    {
        var pem = File.ReadAllText(pemFile);
        var signature = Signer.Sign(pem, ReadPassword("Frase-passe: "), File.ReadAllBytes(file));
        File.WriteAllText(file + UpdateSignature.SignatureSuffix, signature, Encoding.ASCII);
        Console.WriteLine("assinado: " + file + UpdateSignature.SignatureSuffix);
        return 0;
    }

    private static int VerifyFile(string keysFile, string file, string sigFile)
    {
        var keys = UpdateSignature.ParseKeys(File.ReadAllText(keysFile));
        if (keys.Count == 0)
        {
            Console.Error.WriteLine($"recusado: {keysFile} não tem nenhuma linha de chave");
            return 1;
        }
        if (UpdateSignature.Verify(File.ReadAllBytes(file), File.ReadAllText(sigFile).Trim(), keys))
        {
            Console.WriteLine("válida");
            return 0;
        }
        Console.Error.WriteLine("recusado: assinatura inválida para as chaves de " + keysFile);
        return 1;
    }

    private static string ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? "";
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (key.KeyChar != '\0') text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}
