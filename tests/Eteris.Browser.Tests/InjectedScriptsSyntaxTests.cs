using System.Diagnostics;
using System.IO;
using Eteris.Browser.Browser;
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
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void InjectedScript_IsValidJavaScript(string name, string script)
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
