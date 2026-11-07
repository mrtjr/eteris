using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Regra do motor (docs/motor.md, P1): nunca pedir ao Chromium uma captura com «clip». Com clip (e escala), o
// PageHandler::CaptureScreenshot aplica emulação de dispositivo à página que está À VISTA durante a fotografia; na janela
// própria da página (HwndHost) isso vê-se: a página fica em branco e minúscula por 50–200 ms. Recorta-se/reduz-se no .NET.
public class CaptureRulesTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "publish"));

    // Argumentos de primeiro nível de cada chamada a CaptureScreenshotAsync (os parênteses aninhados contam-se).
    public static IEnumerable<IReadOnlyList<string>> Calls(string code)
    {
        foreach (Match m in Regex.Matches(code, @"CaptureScreenshotAsync\s*\("))
        {
            var args = new List<string>();
            var depth = 0;
            var start = m.Index + m.Length;
            for (var i = start; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '(' or '{' or '[') depth++;
                else if (c is ')' or '}' or ']')
                {
                    if (depth == 0)
                    {
                        if (code[start..i].Trim().Length > 0 || args.Count > 0) args.Add(code[start..i].Trim());
                        break;
                    }
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    args.Add(code[start..i].Trim());
                    start = i + 1;
                }
            }
            yield return args;
        }
    }

    // Uma chamada passa um clip se o 3.º argumento posicional (ou «clip:») não for null.
    public static bool PassesClip(IReadOnlyList<string> args)
    {
        var named = args.FirstOrDefault(a => Regex.IsMatch(a, @"^clip\s*:"));
        if (named is not null) return !Regex.IsMatch(named, @"^clip\s*:\s*null$");
        var positional = args.TakeWhile(a => !Regex.IsMatch(a, @"^\w+\s*:(?!:)")).ToList();
        return positional.Count >= 3 && positional[2] != "null";
    }

    [Theory]
    [InlineData("client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Jpeg, 70, clip, true, false)", true)]
    [InlineData("client.Page.CaptureScreenshotAsync(F.Jpeg, 70, new Viewport { X = 0, Scale = s }, true)", true)]
    [InlineData("client.Page.CaptureScreenshotAsync(F.Png, clip: v)", true)]
    [InlineData("client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Jpeg, 70, null, true, false, true)", false)]
    [InlineData("client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png)", false)]
    [InlineData("client.Page.CaptureScreenshotAsync(F.Png, clip: null)", false)]
    [InlineData("client.Page.CaptureScreenshotAsync()", false)]
    public void PassesClip_DetectsClipArguments(string code, bool expected)
        => Assert.Equal(expected, PassesClip(Assert.Single(Calls(code))));

    [Fact]
    public void SourceTree_HasScreenshotCalls()
        => Assert.Contains(SourceFiles(), f => Calls(File.ReadAllText(f)).Any());

    [Fact]
    public void ProductionCode_NeverCapturesWithAClip()
    {
        var offenders = SourceFiles()
            .Where(f => Calls(File.ReadAllText(f)).Any(PassesClip))
            .Select(f => Path.GetRelativePath(Root, f))
            .ToList();
        Assert.True(offenders.Count == 0,
            "CaptureScreenshotAsync com clip (emula o dispositivo na página à vista e faz a página piscar; recortar no .NET):\n"
            + string.Join("\n", offenders));
    }
}
