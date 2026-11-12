using System.IO;
using System.Runtime.CompilerServices;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O ambiente da bateria, posto antes de qualquer teste correr. A bateria nunca corre sobre o perfil do dono (a regra de
// build/perf/README.md): o AppPaths aponta para uma pasta temporária. E o resultado deixa de depender da consola de
// quem corre: saem do processo as variáveis ETERIS_* (interruptores de teste do produto, TestSwitches).
internal static class TestEnvironment
{
    public static string ProfileRoot { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // 1. As variáveis primeiro: desde a 0.2.15 o WindowsReauth só aceita o ETERIS_TEST_REAUTH num perfil que não é o
        // de omissão, e a bateria passa a correr num desses (ponto 2).
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Where(k => k.StartsWith("ETERIS_", StringComparison.OrdinalIgnoreCase)).ToList())
            Environment.SetEnvironmentVariable(name, null);

        // 2. Uma pasta temporária que não se cria: fica como no CI, onde a pasta por omissão não existe (o StrictSiteStore
        // falha a abrir e o AppData trata isso como «sem dados»).
        ProfileRoot = Path.Combine(Path.GetTempPath(), "eteris-testes-" + Guid.NewGuid().ToString("N"));
        AppPaths.UseRoot(ProfileRoot);

        // 3. Se algum teste a criou, sai com o processo.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(ProfileRoot)) Directory.Delete(ProfileRoot, recursive: true);
            }
            catch (Exception) { }
        };
    }
}
