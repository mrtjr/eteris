using System.IO;

namespace Eteris.Browser.Tests;

// Guarda do passo A de docs/sandbox.md («Regras para o código»): JavaScript nas páginas só pelo DevTools
// (Browser/PageScript.cs). As APIs do processo filho do CefSharp passam por um processo .NET que não corre dentro do
// sandbox; com o processo filho nativo falham em silêncio, e os testes continuariam verdes.
public class SandboxRulesTests
{
    private static readonly string Root = FindRoot();

    public static readonly string[] ForbiddenApis =
    [
        "EvaluateScriptAsync", "ExecuteScriptAsync", "ExecuteJavaScriptAsync", "EvaluateScriptAsPromiseAsync",
        "IRenderProcessMessageHandler", "JavascriptObjectRepository", "JavascriptMessageReceived", "RegisterJsObject",
    ];

    // Parte do próprio ficheiro de código: os testes podem correr a partir de uma pasta de compilação fora do repositório.
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

    // Linhas que usam uma das APIs proibidas; os comentários (// ...) podem citá-las, para explicar a regra.
    public static IEnumerable<(int Line, string Api)> Violations(IEnumerable<string> lines)
    {
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var api in ForbiddenApis)
                if (line.Contains(api, StringComparison.Ordinal)) yield return (number, api);
        }
    }

    [Fact]
    public void Violations_FlagCallsButNotComments()
    {
        var found = Violations(
        [
            "// O EvaluateScriptAsync do CefSharp corre através do processo filho",
            "    // browser.ExecuteScriptAsync(\"1\")",
            "var r = await browser.EvaluateScriptAsync(\"document.title\");",
            "public sealed class Handler : IRenderProcessMessageHandler",
        ]).ToList();
        Assert.Equal([(3, "EvaluateScriptAsync"), (4, "IRenderProcessMessageHandler")], found);
    }

    [Fact]
    public void SourceTree_IsScanned()
        => Assert.Contains(SourceFiles(), f => Path.GetFileName(f) == "PageScript.cs");

    [Fact]
    public void ProductionCode_NeverUsesTheCefSharpSubprocessJavascriptApis()
    {
        var offenders = SourceFiles()
            .SelectMany(file => Violations(File.ReadLines(file)).Select(v => $"{Path.GetRelativePath(Root, file)}:{v.Line}: {v.Api}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "APIs do processo filho do CefSharp no código (docs/sandbox.md, «Regras para o código»; usar Browser/PageScript.cs):\n"
            + string.Join("\n", offenders));
    }
}
