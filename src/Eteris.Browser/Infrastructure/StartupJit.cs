using System.IO;
using System.Runtime;

namespace Eteris.Browser.Infrastructure;

// JIT do arranque em vários núcleos (System.Runtime.ProfileOptimization, do próprio .NET): cada arranque grava no perfil
// que métodos foram compilados e o seguinte compila-os de antemão em threads de fundo, antes de o fio da interface
// precisar deles. O código do Éteris e das bibliotecas (CefSharp, Mvvm, Sqlite, Velopack…) é IL sem ReadyToRun: até à
// 1.ª pintura o JIT compilava ~4 200 métodos (~400 ms de CPU), e a amostragem do fio da interface mostrava o clrjit e o
// coreclr em quase todo o tempo que não era espera. Um ficheiro de outra versão do Éteris não serve e é refeito por si.
//
// Privacidade: o ficheiro diz que métodos do programa correram (nada do utilizador, mas o método da janela privada, das
// transferências ou das notas diria que funções se usaram). Por isso a gravação só cobre o arranque: para pouco depois
// da 1.ª página (Stop, que grava o ficheiro nesse momento; sem ele, o .NET continuava a gravar até ao fim do processo,
// medido) ou logo que abre uma janela privada, e um arranque já privado (--private) não grava nem usa o ficheiro.
public static class StartupJit
{
    public const string FileName = "arranque.jitprofile";

    // Quanto tempo depois da 1.ª página a gravação continua (o resto do arranque: lista de saltos, atualizações, abas
    // restauradas em fundo).
    public static readonly TimeSpan RecordAfterFirstPaint = TimeSpan.FromSeconds(5);

    private static int _state; // 0 = nunca começou, 1 = a gravar, 2 = parado

    // Um arranque privado não grava: tudo o que corre nele seria da sessão privada.
    public static bool ShouldRecord(IEnumerable<string> args) => !StartupArgs.IsPrivate(args);

    // Depois da instância única: um 2.º lançamento (que só entrega um link e sai) não regrava o perfil.
    public static void Start(string profileRoot)
    {
        try
        {
            Directory.CreateDirectory(profileRoot);
            ProfileOptimization.SetProfileRoot(profileRoot);
            ProfileOptimization.StartProfile(FileName);
            Interlocked.CompareExchange(ref _state, 1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Para a gravação e grava já o ficheiro: daqui em diante nada do que se usa na sessão fica nele. Só a 1.ª chamada
    // conta; sem Start, não faz nada.
    public static void Stop()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1) return;
        try { ProfileOptimization.StartProfile(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StartupTrace.Mark("jit.parado");
    }
}
