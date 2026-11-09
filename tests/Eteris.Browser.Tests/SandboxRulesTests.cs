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
        // A propriedade do browser (sem o «I» da interface) e as funções JavaScript devolvidas ao .NET.
        "RenderProcessMessageHandler =", "IJavascriptCallback",
    ];

    // Nas páginas internas (src/Eteris.Browser/**/*.html e *.js): o objeto CefSharp que o processo filho .NET injeta
    // (CefSharp.PostMessage, CefSharp.BindObjectAsync…) não existe com o processo filho nativo.
    public const string PageScriptCefSharpObject = "CefSharp.";

    // Parte do próprio ficheiro de código: os testes podem correr a partir de uma pasta de compilação fora do repositório.
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static IEnumerable<string> SourceFiles(string pattern = "*.cs")
        => Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), pattern, SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "publish"));

    // Scripts nas páginas do motor de vídeo (WebView2): só pelo Browser/VideoPageScript.cs. O WebView2 não tem o processo
    // filho .NET do CefSharp (a razão da regra acima não se aplica), mas o canal fica num só sítio.
    public static readonly string[] VideoEngineScriptApis =
    [
        "CallDevToolsProtocolMethodAsync", "ExecuteScriptAsync", "AddScriptToExecuteOnDocumentCreatedAsync",
    ];

    public const string VideoScriptChannel = "VideoPageScript.cs";

    // Linhas que usam uma das APIs proibidas; os comentários (// ...) podem citá-las, para explicar a regra.
    public static IEnumerable<(int Line, string Api)> Violations(IEnumerable<string> lines, IReadOnlyList<string>? apis = null)
    {
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var api in apis ?? ForbiddenApis)
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

    [Fact]
    public void Violations_FlagTheHandlerPropertyAndJavascriptCallbacks()
    {
        var found = Violations(
        [
            "browser.RenderProcessMessageHandler = new Handler();",
            "async Task Done(IJavascriptCallback callback) { }",
            "// browser.RenderProcessMessageHandler = null; só num comentário",
        ]).ToList();
        Assert.Equal([(1, "RenderProcessMessageHandler ="), (2, "IJavascriptCallback")], found);
    }

    [Fact]
    public void InternalPages_NeverUseTheCefSharpObject()
    {
        var pages = SourceFiles("*.html").Concat(SourceFiles("*.js")).ToList();
        Assert.Contains(pages, f => Path.GetFileName(f) == "newtab.html");
        var offenders = pages
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (Line: i + 1, Text: line))
                .Where(l => l.Text.Contains(PageScriptCefSharpObject, StringComparison.Ordinal))
                .Select(l => $"{Path.GetRelativePath(Root, file)}:{l.Line}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Objeto CefSharp do processo filho .NET nas páginas internas (docs/sandbox.md, «Regras para o código»):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void VideoEngine_InjectsScriptsOnlyThroughItsChannel()
    {
        var offenders = SourceFiles()
            .Where(file => Path.GetFileName(file) != VideoScriptChannel)
            .SelectMany(file => Violations(File.ReadLines(file), VideoEngineScriptApis)
                .Select(v => $"{Path.GetRelativePath(Root, file)}:{v.Line}: {v.Api}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Scripts no motor de vídeo fora do Browser/VideoPageScript.cs:\n" + string.Join("\n", offenders));
        var channel = SourceFiles().Single(f => Path.GetFileName(f) == VideoScriptChannel);
        Assert.NotEmpty(Violations(File.ReadLines(channel), VideoEngineScriptApis));
    }

    [Fact]
    public void VideoEngineRule_FlagsTheWebView2Calls()
    {
        var found = Violations(
        [
            "// core.ExecuteScriptAsync(\"1\") só no canal",
            "await core.CallDevToolsProtocolMethodAsync(\"Page.enable\", \"{}\");",
            "await core.AddScriptToExecuteOnDocumentCreatedAsync(script);",
        ], VideoEngineScriptApis).ToList();
        Assert.Equal([(2, "CallDevToolsProtocolMethodAsync"), (3, "AddScriptToExecuteOnDocumentCreatedAsync")], found);
    }
}
