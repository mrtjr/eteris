using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// Os pedidos DevTools partilham a numeração do CefSharp por browser e o CEF só aceita números crescentes (medido com o
// canal do cofre: «Generated MessageId 14 doesn't match returned Message Id 12»). Todos passam por DevToolsCalls.Run, que
// numera e põe na fila juntos e por ordem.
public class DevToolsCallsTests
{
    private static readonly Regex s_call = new(
        @"\b\w+\.(Page|Runtime|Emulation|Network|Storage|Browser|DOM|DOMStorage|Target|Input|Security|Fetch|Log|Debugger|Overlay|CSS|Autofill)\.\w+Async\(|\.ExecuteDevToolsMethodAsync\(",
        RegexOptions.CultureInvariant);

    public static IEnumerable<string> Offenders(string file, IEnumerable<string> lines)
        => lines.Select((text, i) => (text, line: i + 1))
            .Where(l => !l.text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => s_call.IsMatch(l.text) && !l.text.Contains("DevToolsCalls.Run(", StringComparison.Ordinal))
            .Select(l => $"{file}:{l.line}");

    [Fact]
    public void Offenders_FlagsBareCalls()
    {
        var found = Offenders("x", [
            "await client.Page.EnableAsync();",
            "await DevToolsCalls.Run(() => client.Page.EnableAsync());",
            "// client.Runtime.EvaluateAsync(\"1\") no comentário",
            "var r = await client.ExecuteDevToolsMethodAsync(\"DOMStorage.getDOMStorageItems\", p);",
            "client.Page.FrameNavigated += OnFrameNavigated;",
        ]).ToList();
        Assert.Equal(["x:1", "x:4"], found);
    }

    [Fact]
    public void EveryDevToolsCall_GoesThroughTheGate()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Offenders(Path.GetRelativePath(src, f), File.ReadLines(f)))
            .ToList();
        Assert.True(offenders.Count == 0, "Pedidos DevTools fora de DevToolsCalls.Run:\n" + string.Join("\n", offenders));
    }

    // Fora da thread do CEF, o pedido corre já (numerar e pôr na fila na mesma thread, dentro do portão).
    [Fact]
    public async Task OffTheCefThread_RunsInline()
    {
        var thread = -1;
        var result = await DevToolsCalls.Run(() =>
        {
            thread = Environment.CurrentManagedThreadId;
            return Task.FromResult(42);
        });
        Assert.Equal(42, result);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => DevToolsCalls.Run(() => throw new ObjectDisposedException("IBrowser")));
    }
}
