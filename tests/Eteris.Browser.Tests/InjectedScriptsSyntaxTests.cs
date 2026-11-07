using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Os scripts que o Éteris injeta nas páginas são texto C#: um erro de sintaxe só aparecia com a app a correr
// (o modo de leitura deixou de abrir assim). Aqui cada um é compilado pelo Node; sem Node instalado, o teste passa.
public class InjectedScriptsSyntaxTests
{
    public static TheoryData<string, string> Scripts => new()
    {
        { "leitor (deteção)", ReaderScripts.Detect },
        { "leitor (abrir/fechar)", ReaderScripts.ToggleWith("sepia", 22, false) },
        { "seguir links pelo teclado", LinkHints.Script },
        { "escurecer (ligar)", DarkPages.Live(true) },
        { "escurecer (desligar)", DarkPages.Live(false) },
        { "multimédia (estado)", MediaInfo.Script },
        { "multimédia (tocar/pausar)", MediaInfo.ToggleScript },
        { "multimédia (pausar)", MediaInfo.PauseScript },
        { "áudio", AudioIndicator.AudibleScript },
        { "filtros cosméticos", CosmeticScripts.Inject(".anuncio { display: none !important; }") },
        { "filtros cosméticos (antes da página)", CosmeticScripts.InjectEarly(".anuncio { display: none !important; }") },
        { "corpo async (DevTools)", Eteris.Browser.Browser.PageScript.Expression("return await Promise.resolve(1);", asyncBody: true) },
        { "vídeo só em H.264 (deteção)", VideoEngine.WatchScript },
        { "último clique (pop-unders)", PopupPolicy.ActivationScript },
        { "escurecer (antes da página)", DarkPages.EarlyScript },
        { "espreitar (extração do artigo)", LinkPreview.Script },
        { "espreitar (Alt+clique)", LinkPreview.AltClickScript },
        { "scripts de um site (guarda do topo)", HostScriptRegistry.Guard("www.publico.pt",
            ScriptletScripts.Wrap("testMark(1)", "https://www.publico.pt/") + "\n;\n" + CosmeticScripts.InjectEarly(".a { display: none }") + DarkPages.EarlyScript)! },
        { "script base (por protocolo)", HostScriptRegistry.ForProtocols(["http:", "https:"], VideoEngine.WatchScript + "\n;\n" + PopupPolicy.ActivationScript) },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void InjectedScript_IsValidJavaScript(string name, string script) => CheckSyntax(name, script);

    [Fact]
    public void NewTabScript_IsValidJavaScript()
    {
        var html = NewTabPage.Render([], SearchEngine.DuckDuckGo, DateTime.Today);
        var match = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        CheckSyntax("nova aba", match.Groups[1].Value);
    }

    private static void CheckSyntax(string name, string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"eteris-script-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            var start = new ProcessStartInfo("node", ["-e", "new Function(require('fs').readFileSync(process.argv[1], 'utf8'))", file])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process? node;
            try { node = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { return; } // Node não instalado: não há como verificar aqui.
            if (node is null) return;
            var error = node.StandardError.ReadToEnd();
            node.WaitForExit(30_000);
            Assert.True(node.ExitCode == 0, $"O script «{name}» tem um erro de sintaxe:\n{error}");
        }
        finally { File.Delete(file); }
    }
}
