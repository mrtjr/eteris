using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C1, guardas (docs/palavras-passe.md e docs/sandbox.md): o script do cofre só entra por um mundo isolado
// (Page.createIsolatedWorld + Runtime.evaluate com o contextId desse mundo), nunca pelo Runtime.enable (as páginas
// conseguem detetá-lo) nem pelo registo antes da página; e não escreve no DOM fora do preenchimento pedido.
public class VaultChannelGuardTests
{
    private static readonly string Root = GoogleSyncTests.FindRoot();
    private static readonly string Src = Path.Combine(Root, "src", "Eteris.Browser");

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Src, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "publish"));

    private static IEnumerable<(string File, int Line, string Text)> CodeLines(string file)
        => File.ReadLines(file).Select((text, i) => (file, i + 1, text)).Where(l => !l.text.TrimStart().StartsWith("//", StringComparison.Ordinal));

    // O Runtime.enable liga o envio de todas as mensagens da consola e é procurado pelos anti-robôs: nunca no Éteris.
    [Fact]
    public void NoRuntimeEnable_Anywhere()
    {
        var offenders = SourceFiles().SelectMany(CodeLines)
            .Where(l => l.Text.Contains("Runtime.EnableAsync", StringComparison.Ordinal) || l.Text.Contains("\"Runtime.enable\"", StringComparison.Ordinal))
            .Select(l => $"{Path.GetRelativePath(Root, l.File)}:{l.Line}")
            .ToList();
        Assert.True(offenders.Count == 0, "Runtime.enable no código:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Channel_EvaluatesOnlyInTheIsolatedContext()
    {
        var channel = Path.Combine(Src, "Vault", "VaultChannel.cs");
        var lines = CodeLines(channel).ToList();
        var evaluations = lines.Where(l => l.Text.Contains("Runtime.EvaluateAsync(", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(evaluations);
        Assert.All(evaluations, l => Assert.Contains("contextId:", l.Text));
        Assert.Contains(lines, l => l.Text.Contains("CreateIsolatedWorldAsync(", StringComparison.Ordinal) && l.Text.Contains("WorldName", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("AddBindingAsync(", StringComparison.Ordinal) && l.Text.Contains("executionContextId:", StringComparison.Ordinal));
        // Nunca o registo antes da página (seria o mundo da página) nem o grantUniversalAccess.
        Assert.DoesNotContain(lines, l => l.Text.Contains("AddScriptToEvaluateOnNewDocument", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => Regex.IsMatch(l.Text, @"grantUniveralAccess:\s*true"));
    }

    // O script do cofre só sai de VaultScripts para o VaultChannel.
    [Fact]
    public void CofreScript_OnlyReachesThePageThroughTheChannel()
    {
        var users = SourceFiles()
            .Where(f => CodeLines(f).Any(l => l.Text.Contains("VaultScripts.Cofre", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["VaultChannel.cs"], users);
    }

    // As abas ligam-se ao cofre só por MainWindow.Vault.cs; o arranque não constrói nada do cofre.
    [Fact]
    public void Startup_NeverBuildsTheChannel()
    {
        foreach (var startup in new[] { "App.xaml.cs", "MainWindow.xaml.cs", "Program.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Src, startup));
            Assert.DoesNotContain("new VaultChannel", source);
            Assert.DoesNotContain("VaultScripts", source);
        }
    }

    // O que o script do cofre nunca faz fora do preenchimento pedido: escrever no DOM (atributos, classes, estilos,
    // nós), mexer no foco, disparar eventos, falar com a rede ou guardar coisas na página. Dentro da zona «preencher»
    // só o setter nativo do valor, o foco e os eventos input/change.
    public static readonly string[] DomWrites =
    [
        "setAttribute", "removeAttribute", "toggleAttribute", "classList", ".style", "innerHTML", "outerHTML", "textContent =",
        "innerText =", "appendChild", "insertBefore", ".append(", ".prepend(", ".remove(", "replaceWith", "replaceChildren",
        "dataset", "createElement", "attachShadow", "document.write", "insertAdjacent", ".checked =", "setRangeText",
        "execCommand", "scrollIntoView", ".value =", "dispatchEvent", ".focus(", ".blur(", ".click(", ".select(",
        "console.", "postMessage", "localStorage", "sessionStorage", "indexedDB", "document.cookie", "fetch(", "XMLHttpRequest",
        "WebSocket", "sendBeacon", "new Image", "window.open", "location.", "history.",
    ];

    public static readonly string[] AllowedInFill = [".focus(", "dispatchEvent"];

    public static IEnumerable<(int Line, string Token)> ScriptViolations(string script)
    {
        var inFill = false;
        var number = 0;
        foreach (var raw in script.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.StartsWith("// <preencher>", StringComparison.Ordinal)) { inFill = true; continue; }
            if (line.StartsWith("// </preencher>", StringComparison.Ordinal)) { inFill = false; continue; }
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var token in DomWrites)
                if (Writes(line, token) && !(inFill && AllowedInFill.Contains(token))) yield return (number, token);
        }
    }

    // «x.value =» é escrever; «x.value === y» é só comparar.
    private static bool Writes(string line, string token)
        => token.EndsWith(" =", StringComparison.Ordinal)
            ? Regex.IsMatch(line, Regex.Escape(token[..^2]) + @"\s*=(?!=)")
            : line.Contains(token, StringComparison.Ordinal);

    [Fact]
    public void ScriptViolations_FlagsWritesOutsideTheFillZone()
    {
        var found = ScriptViolations(string.Join('\n',
            "// o campo fica com .style.outline (comentário: não conta)",
            "el.setAttribute('data-x', 1);",
            "// <preencher>",
            "field.focus();",
            "field.dispatchEvent(new Event('input', { bubbles: true }));",
            "field.classList.add('x');",
            "// </preencher>",
            "field.focus();",
            "if (field.value === '') x = 1;",
            "field.value = 'x';")).ToList();
        Assert.Equal([(2, "setAttribute"), (6, "classList"), (8, ".focus("), (10, ".value =")], found);
    }

    [Fact]
    public void CofreScript_NeverWritesToTheDomOutsideTheRequestedFill()
    {
        var script = VaultScripts.Cofre("eabc");
        var found = ScriptViolations(script).ToList();
        Assert.True(found.Count == 0, "O script do cofre escreve na página fora do preenchimento:\n"
            + string.Join("\n", found.Select(f => $"linha {f.Line}: {f.Token}")));
        // A ligação sai do mundo isolado logo ao arrancar (nem lá fica à vista) e o objeto do cofre não se reescreve.
        Assert.Contains("delete globalThis[", script);
        Assert.Contains("Object.defineProperty(globalThis, '__cofre'", script);
    }
}
