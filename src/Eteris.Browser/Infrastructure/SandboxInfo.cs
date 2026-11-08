using System.IO;
using System.Runtime.InteropServices;

namespace Eteris.Browser.Infrastructure;

// Sandbox dos processos das páginas (docs/sandbox.md, passo D). Quando o Éteris arranca pelo bootstrap da CEF (o exe é
// o bootstrap.exe renomeado), a DLL nativa com o nome do exe (build/sandbox/client) arranca o .NET e guarda o ponteiro
// que o bootstrap lhe entregou; aqui lê-se pela exportação EterisSandboxInfo. Pelo apphost do .NET (desenvolvimento,
// testes) não há essa DLL nativa: o ponteiro fica zero e o motor corre como sempre, sem sandbox.
public static class SandboxInfo
{
    // Diagnóstico: arranca sem sandbox mesmo pelo bootstrap (como o --no-sandbox do Chrome).
    public const string NoSandboxSwitch = "--eteris-no-sandbox";

    public const string ExportName = "EterisSandboxInfo";

    private static readonly Lazy<nint> pointer = new(() => Read(Environment.ProcessPath, Environment.GetCommandLineArgs()));
    private static readonly Lazy<bool> bootstrap = new(() => FindExport(Environment.ProcessPath) != 0);

    public static nint Pointer => pointer.Value;

    // O Éteris arrancou pelo bootstrap (a DLL nativa está carregada), com ou sem sandbox: os processos filhos podem ser o
    // próprio exe (com --eteris-no-sandbox, a publicação com sandbox já não traz o processo filho .NET do CefSharp).
    public static bool LaunchedByBootstrap => bootstrap.Value;

    // A DLL nativa tem o nome do exe (regra do bootstrap: <exe sem extensão>.dll na mesma pasta).
    public static string? ClientDllPath(string? processPath)
        => string.IsNullOrEmpty(processPath) ? null : Path.ChangeExtension(processPath, ".dll");

    public static bool Disabled(IEnumerable<string> args)
        => args.Any(a => a.Equals(NoSandboxSwitch, StringComparison.OrdinalIgnoreCase));

    internal static nint Read(string? processPath, IEnumerable<string> args)
    {
        if (Disabled(args)) return 0;
        var export = FindExport(processPath);
        if (export == 0) return 0;
        unsafe
        {
            return ((delegate* unmanaged<nint>)export)();
        }
    }

    // Só um módulo JÁ carregado (pelo bootstrap): nunca carregar uma DLL aqui.
    internal static nint FindExport(string? processPath)
    {
        if (ClientDllPath(processPath) is not { } dll) return 0;
        var module = GetModuleHandleW(dll);
        return module == 0 ? 0 : GetProcAddress(module, ExportName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string procName);
}
