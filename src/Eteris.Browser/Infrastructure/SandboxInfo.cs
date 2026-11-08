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

    public static nint Pointer => pointer.Value;

    // A DLL nativa tem o nome do exe (regra do bootstrap: <exe sem extensão>.dll na mesma pasta).
    public static string? ClientDllPath(string? processPath)
        => string.IsNullOrEmpty(processPath) ? null : Path.ChangeExtension(processPath, ".dll");

    public static bool Disabled(IEnumerable<string> args)
        => args.Any(a => a.Equals(NoSandboxSwitch, StringComparison.OrdinalIgnoreCase));

    internal static nint Read(string? processPath, IEnumerable<string> args)
    {
        if (Disabled(args) || ClientDllPath(processPath) is not { } dll) return 0;
        // Só um módulo JÁ carregado (pelo bootstrap): nunca carregar uma DLL aqui.
        var module = GetModuleHandleW(dll);
        if (module == 0) return 0;
        var export = GetProcAddress(module, ExportName);
        if (export == 0) return 0;
        unsafe
        {
            return ((delegate* unmanaged<nint>)export)();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string procName);
}
