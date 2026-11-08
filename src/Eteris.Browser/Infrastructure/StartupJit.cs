using System.IO;
using System.Runtime;

namespace Eteris.Browser.Infrastructure;

// JIT do arranque em vários núcleos (System.Runtime.ProfileOptimization, do próprio .NET): cada arranque grava no perfil
// que métodos foram compilados e o seguinte compila-os de antemão em threads de fundo, antes de o fio da interface
// precisar deles. O código do Éteris e das bibliotecas (CefSharp, Mvvm, Sqlite, Velopack…) é IL sem ReadyToRun: até à
// 1.ª pintura o JIT compilava ~4 200 métodos (~400 ms de CPU), e a amostragem do fio da interface mostrava o clrjit e o
// coreclr em quase todo o tempo que não era espera. O ficheiro só tem nomes de métodos do programa (nada do utilizador);
// um ficheiro de outra versão do Éteris não serve e é refeito por si.
public static class StartupJit
{
    public const string FileName = "arranque.jitprofile";

    // Depois da instância única: um 2.º lançamento (que só entrega um link e sai) não regrava o perfil.
    public static void Start(string profileRoot)
    {
        try
        {
            Directory.CreateDirectory(profileRoot);
            ProfileOptimization.SetProfileRoot(profileRoot);
            ProfileOptimization.StartProfile(FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
