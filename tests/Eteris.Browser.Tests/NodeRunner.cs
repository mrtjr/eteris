using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Eteris.Browser.Tests;

// O único sítio que lança o Node nos testes (NodeRunnerTests.NoTestStartsNodeOutsideTheRunner). Sem o Node no PATH os
// testes falham com uma mensagem clara — antes, cada cópia do ajudante devolvia nulo e o teste passava em branco.
internal static class NodeRunner
{
    private const string Missing = "o Node não está no PATH: os testes dos scripts precisam dele (README.md, «Desenvolvimento»)";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Corre o script e devolve o que escreveu na saída, com «\n» como fim de linha; falha com o stderr se o Node sair
    // com um código diferente de 0.
    public static string Run(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"eteris-node-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            var (exitCode, output, error) = Start("node", [file], Timeout, Missing);
            if (exitCode != 0) throw new Xunit.Sdk.XunitException($"O Node saiu com o código {exitCode}:\n{error}");
            return output.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        finally { File.Delete(file); }
    }

    // Compila o script (sem o correr) e falha com o erro de sintaxe.
    public static void CheckSyntax(string name, string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"eteris-node-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            var (exitCode, _, error) = Start("node", ["-e", "new Function(require('fs').readFileSync(process.argv[1], 'utf8'))", file], Timeout, Missing);
            if (exitCode != 0) throw new Xunit.Sdk.XunitException($"O script «{name}» tem um erro de sintaxe:\n{error}");
        }
        finally { File.Delete(file); }
    }

    // Lança a ferramenta, lê as duas saídas em paralelo (UTF-8) e espera até ao prazo; no fim do prazo mata a árvore de
    // processos e falha. Uma ferramenta que não existe falha com «missing».
    internal static (int ExitCode, string Output, string Error) Start(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, string missing)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new Xunit.Sdk.XunitException(missing); }
        catch (Win32Exception) { throw new Xunit.Sdk.XunitException(missing); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // acabou entretanto
                catch (Win32Exception) { } // já a sair
                throw new Xunit.Sdk.XunitException($"{executable} não acabou em {timeout.TotalSeconds:0} s");
            }
            process.WaitForExit(); // as saídas redirecionadas acabam de chegar
            return (process.ExitCode, output.Result, error.Result);
        }
    }
}
