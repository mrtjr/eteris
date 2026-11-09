using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Nenhum ficheiro de texto versionado leva o caminho do perfil de quem trabalha no repositório (arrumação 0.2.14): o
// repositório é público e o caminho do perfil tem o nome do utilizador do Windows. Nenhum nome está escrito aqui: o teste
// procura o perfil da máquina onde corre (forma longa e nome curto 8.3); no CI apanha o do runner, que é inofensivo.
// Lê `git ls-files`, que é exatamente o que está versionado (sem entrar em build\…\out, publish\, Releases\ nem em notas
// locais por versionar). Nos textos usa-se %USERPROFILE%, %LOCALAPPDATA%, C:\Users\<utilizador> ou o nome curto fictício
// ABCDEF~1.
public class RepositoryHygieneTests
{
    // Listas de filtros e créditos de terceiros: têm domínios e nomes de autores com qualquer palavra.
    private static readonly string[] ThirdParty = ["src/Eteris.Browser/Shields/lists/", "licenses/"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".cs", ".xaml", ".csproj", ".props", ".targets", ".slnx", ".sln", ".md", ".txt", ".ps1", ".psm1", ".cmd", ".bat",
        ".sh", ".py", ".json", ".yml", ".yaml", ".xml", ".toml", ".lock", ".rs", ".c", ".cc", ".cpp", ".h", ".in", ".manifest",
        ".rc", ".gn", ".patch", ".html", ".htm", ".js", ".mjs", ".css", ".svg", ".gitignore", ".gitattributes", ".editorconfig",
    };

    [Fact]
    public void TextFiles_HaveNoLocalProfilePath()
    {
        var root = RepoRoot();
        var files = TrackedFiles(root);
        if (files is null) return; // sem git não há a lista do que está versionado (raro: o CI e quem publica têm git)
        var profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile.Length == 0) return;
        var needle = ProfileNeedle(profile, ShortPath(profile));
        var hits = new List<string>();
        foreach (var rel in files)
        {
            if (ThirdParty.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!TextExtensions.Contains(Path.GetExtension(rel))) continue;
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue; // apagado na árvore e ainda no índice
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
                if (needle.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}");
        }
        // A mensagem diz só ficheiro:linha, nunca o nome procurado (o registo do CI é público).
        Assert.True(hits.Count == 0,
            @"Caminho do perfil local (use %USERPROFILE%, %LOCALAPPDATA% ou C:\Users\<utilizador>) em: " + string.Join(", ", hits));
    }

    // A agulha: «\Users\<nome>» (ou «/Users/<nome>», com a pasta-mãe real do perfil) na forma longa ou curta 8.3, sem
    // mais letras ou algarismos a seguir; e o nome curto 8.3 sozinho, que com o «~» é distinto o bastante (por exemplo
    // «pasta do repositório (<curto>)»). O nome longo sozinho não se procura: seria uma palavra qualquer.
    internal static Regex ProfileNeedle(string profile, string shortProfile)
    {
        profile = Path.TrimEndingDirectorySeparator(profile);
        shortProfile = Path.TrimEndingDirectorySeparator(shortProfile);
        var leaf = Path.GetFileName(profile);
        var shortLeaf = Path.GetFileName(shortProfile);
        var parent = Path.GetFileName(Path.GetDirectoryName(profile) ?? "");
        if (leaf.Length == 0) return new Regex("(?!)");
        var names = new List<string> { Regex.Escape(leaf) };
        var hasShort = shortLeaf.Length > 0 && !string.Equals(shortLeaf, leaf, StringComparison.OrdinalIgnoreCase);
        if (hasShort) names.Add(Regex.Escape(shortLeaf));
        var dir = parent.Length > 0 ? Regex.Escape(parent) : "Users";
        var pattern = $@"[\\/]{dir}[\\/](?:{string.Join("|", names)})(?![\p{{L}}\p{{N}}])";
        if (hasShort && shortLeaf.Contains('~'))
            pattern += $@"|(?<![\p{{L}}\p{{N}}]){Regex.Escape(shortLeaf)}(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Os casos usam «<u>» (nome longo) e «<s>» (nome curto) em vez de um caminho escrito, para este ficheiro não ter
    // nenhum caminho de perfil nem numa máquina cujo utilizador se chame como o do exemplo.
    [Theory]
    [InlineData(@"a cópia em `C:\Users\<u>\…`", true)]
    [InlineData(@"c:/users/<u>/projeto", true)]
    [InlineData(@"Pasta do projeto: C:\Users\<u>", true)]
    [InlineData(@"(`C:\Users\<u>`)", true)]
    [InlineData(@"forma curta (C:\Users\<s>\AppData)", true)]
    [InlineData(@"pasta do repositório (<s>); sem nomes curtos", true)]
    [InlineData(@"%USERPROFILE%\Desktop e %LOCALAPPDATA%\EterisBrowser", false)]
    [InlineData(@"C:\Users\<utilizador>\Desktop", false)]
    [InlineData(@"C:\Users\ABCDEF~1\AppData", false)]
    [InlineData(@"C:\Users\<u>s\AppData", false)]
    [InlineData(@"<s>2 e x<s>", false)]
    [InlineData(@"o <u> escolheu", false)]
    public void ProfileNeedle_FindsTheLongAndShortForms(string template, bool expected)
    {
        const string name = "Usu\u00e1rio", shortName = "USURIO~1";
        var needle = ProfileNeedle(@"C:\Users\" + name, @"C:\Users\" + shortName);
        Assert.Equal(expected, needle.IsMatch(template.Replace("<u>", name).Replace("<s>", shortName)));
    }

    // Sem nome curto diferente (por exemplo um nome ASCII curto, ou o runner do CI), só a forma «\Users\<nome>» conta.
    [Fact]
    public void ProfileNeedle_WithoutAShortName_OnlyLooksForThePath()
    {
        const string name = "ci" + "runner";
        var needle = ProfileNeedle(@"C:\Users\" + name, @"C:\Users\" + name);
        Assert.Matches(needle, @"C:\Users\" + name + @"\AppData");
        Assert.DoesNotMatch(needle, "o " + name + " do CI");
        Assert.DoesNotMatch(needle, @"C:\Users\" + name + "2");
    }

    private static List<string>? TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-C", root, "ls-files", "-z" }) start.ArgumentList.Add(argument);
        Process? git;
        try { git = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return null; } // git não instalado
        if (git is null) return null;
        using (git)
        {
            var error = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            Assert.True(git.WaitForExit(60_000), "git ls-files não acabou em 60 s");
            _ = error.Result;
            // Sem o texto do erro: pode trazer o caminho do repositório, e com ele o do perfil.
            Assert.True(git.ExitCode == 0, $"git ls-files saiu com o código {git.ExitCode}");
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    private static string ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var n = GetShortPathNameW(path, buffer, buffer.Capacity);
        return n > 0 && n < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetShortPathNameW(string path, StringBuilder buffer, int length);

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
