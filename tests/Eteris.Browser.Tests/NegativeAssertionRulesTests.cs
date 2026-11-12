using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Regra de fonte (TFI-07): os ficheiros do repositório têm CRLF na cópia de trabalho do Windows. Uma asserção negativa
// (Assert.DoesNotContain/DoesNotMatch) sobre um texto com «\n» nunca encontra o texto proibido num ficheiro com CRLF e,
// por isso, nunca falha — passava a verde com o defeito lá. Quem a escreve normaliza os fins de linha na mesma linha
// (.Replace("\r\n", "\n")); as classes negadas («[^\n]») e os literais de carácter ('\n') não contam.
public class NegativeAssertionRulesTests
{
    [Theory]
    [InlineData(@"Assert.DoesNotContain(""a:\n    b"", text);", true)]
    [InlineData(@"Assert.DoesNotMatch(@""a\n\s+b"", text);", true)]
    [InlineData(@"Assert.DoesNotContain(""a:\n    b"", text.Replace(""\r\n"", ""\n""));", false)]
    [InlineData(@"Assert.DoesNotMatch(@""foreach[^\n]*Pick\("", text);", false)]
    [InlineData(@"Assert.DoesNotMatch(@""a[^\r\n]*b"", text);", false)]
    [InlineData(@"Assert.DoesNotContain('\n', name);", false)]
    [InlineData(@"Assert.Contains(""a:\n    b"", text);", false)]
    public void IsBlindToCrlf_Table(string line, bool expected) => Assert.Equal(expected, IsBlindToCrlf(line));

    [Fact]
    public void NegativeMultilineAssertions_NormalizeLineEndings()
    {
        var folder = TestsFolder();
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            // Este ficheiro (a tabela) e o FilterListsTests.cs, cujo texto é criado pelo próprio teste.
            if (Path.GetFileName(file) is "NegativeAssertionRulesTests.cs" or "FilterListsTests.cs") continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (IsBlindToCrlf(lines[i])) hits.Add($"{relative}:{i + 1}");
        }
        Assert.True(hits.Count == 0, "Asserções negativas com «\\n» sem normalizar os fins de linha: " + string.Join(", ", hits));
    }

    // Verdadeiro se a linha for uma asserção negativa que ainda tem «\n» depois de tiradas as classes negadas e os
    // literais de carácter, sem normalizar os fins de linha na mesma linha.
    internal static bool IsBlindToCrlf(string line)
    {
        if (!line.Contains("Assert.DoesNotContain(", StringComparison.Ordinal)
            && !line.Contains("Assert.DoesNotMatch(", StringComparison.Ordinal)) return false;
        var rest = Regex.Replace(line, @"\[\^[^\]]*\]", "")
            .Replace(@"'\n'", "", StringComparison.Ordinal)
            .Replace(@"'\r'", "", StringComparison.Ordinal);
        return rest.Contains(@"\n", StringComparison.Ordinal)
               && !line.Contains(@".Replace(""\r\n"", ""\n"")", StringComparison.Ordinal);
    }

    private static string TestsFolder([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
