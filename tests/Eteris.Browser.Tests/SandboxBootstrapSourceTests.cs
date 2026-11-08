using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Passo D de docs/sandbox.md: o bootstrap da CEF arranca todos os processos pela DLL nativa de build/sandbox/client
// (filhos: CefExecuteProcess; principal: .NET pela hostfxr) e o CefSharp leva o patch v2 (build/cefsharp). Estes testes
// guardam o que não se vê sem abrir o navegador: o esquema eteris:// registado igual nos dois lados, as exportações
// que o bootstrap e o .NET procuram, e o patch do CefSharp que liga o sandbox.
public class SandboxBootstrapSourceTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string ClientSource => Read("build", "sandbox", "client", "eteris_client.cpp");

    // Opções do esquema no CefSharp (CefCustomScheme: por omissão Standard | Secure | CorsEnabled) -> nomes da CEF.
    private static readonly Dictionary<string, string> SchemeOptionNames = new()
    {
        ["IsStandard"] = "CEF_SCHEME_OPTION_STANDARD",
        ["IsLocal"] = "CEF_SCHEME_OPTION_LOCAL",
        ["IsDisplayIsolated"] = "CEF_SCHEME_OPTION_DISPLAY_ISOLATED",
        ["IsSecure"] = "CEF_SCHEME_OPTION_SECURE",
        ["IsCorsEnabled"] = "CEF_SCHEME_OPTION_CORS_ENABLED",
        ["IsCSPBypassing"] = "CEF_SCHEME_OPTION_CSP_BYPASSING",
        ["IsFetchEnabled"] = "CEF_SCHEME_OPTION_FETCH_ENABLED",
    };

    public static SortedSet<string> ManagedSchemeOptions(string configurator)
    {
        var block = Regex.Match(configurator, @"new CefCustomScheme\s*\{(?<body>[^}]*)\}");
        Assert.True(block.Success, "CefConfigurator sem RegisterScheme(new CefCustomScheme { ... })");
        var options = new SortedSet<string>(StringComparer.Ordinal)
        {
            "CEF_SCHEME_OPTION_STANDARD", "CEF_SCHEME_OPTION_SECURE", "CEF_SCHEME_OPTION_CORS_ENABLED",
        };
        foreach (Match m in Regex.Matches(block.Groups["body"].Value, @"(?<name>Is\w+)\s*=\s*(?<value>true|false)"))
        {
            var cef = SchemeOptionNames[m.Groups["name"].Value];
            if (m.Groups["value"].Value == "true") options.Add(cef); else options.Remove(cef);
        }
        return options;
    }

    public static SortedSet<string> NativeSchemeOptions(string cpp)
    {
        var call = Regex.Match(cpp, @"AddCustomScheme\(\s*""eteris""\s*,(?<flags>[^;]*)\);");
        Assert.True(call.Success, "sem AddCustomScheme(\"eteris\", ...)");
        return new SortedSet<string>(Regex.Matches(call.Groups["flags"].Value, @"CEF_SCHEME_OPTION_\w+").Select(m => m.Value),
            StringComparer.Ordinal);
    }

    [Fact]
    public void ManagedSchemeOptions_StartFromTheCefSharpDefaults()
    {
        var options = ManagedSchemeOptions("settings.RegisterScheme(new CefCustomScheme { SchemeName = \"x\", IsSecure = false, IsLocal = true });");
        Assert.Equal(new[] { "CEF_SCHEME_OPTION_CORS_ENABLED", "CEF_SCHEME_OPTION_LOCAL", "CEF_SCHEME_OPTION_STANDARD" }, options);
    }

    // Um esquema registado de forma diferente no processo principal e nas páginas parte o eteris:// (páginas internas).
    [Fact]
    public void NativeChildren_RegisterTheEterisSchemeExactlyLikeTheBrowserProcess()
    {
        var managed = ManagedSchemeOptions(Read("src", "Eteris.Browser", "Infrastructure", "CefConfigurator.cs"));
        Assert.Equal(managed, NativeSchemeOptions(ClientSource));
        Assert.Equal(managed, NativeSchemeOptions(Read("build", "sandbox", "subprocess", "subprocess.cpp")));
    }

    // O bootstrap procura RunWinMain na DLL com o nome do exe; o .NET lê o ponteiro do sandbox por EterisSandboxInfo.
    [Fact]
    public void Client_ExportsTheBootstrapEntryAndTheSandboxPointer()
    {
        var cpp = ClientSource;
        Assert.Matches(@"extern ""C"" __declspec\(dllexport\) int RunWinMain\(HINSTANCE [^,]*, LPWSTR, int, void\* sandbox_info,\s*cef_version_info_t\*\)", cpp);
        Assert.Matches(@"extern ""C"" __declspec\(dllexport\) void\* EterisSandboxInfo\(\)", cpp);
        // Filhos: a informação do sandbox chega ao CefExecuteProcess; o principal guarda-a antes de arrancar o .NET.
        Assert.Contains("CefExecuteProcess(args, app.get(), sandbox_info)", cpp);
        var store = cpp.IndexOf("g_sandbox_info = sandbox_info;", StringComparison.Ordinal);
        Assert.True(store > cpp.IndexOf("CefExecuteProcess(args, app.get(), sandbox_info)", StringComparison.Ordinal));
        Assert.True(store < cpp.IndexOf("const int code = RunDotNet(argc, argv);", StringComparison.Ordinal));
    }

    // Processo filho só com um ARGUMENTO --type=<valor> antes de um «--» (como o base::CommandLine do Chromium no
    // Windows: prefixos --, - e /, sem distinção de maiúsculas), nunca «--type=» a meio de outro argumento (um endereço).
    [Fact]
    public void Client_DetectsChildrenByArgumentNotBySubstring()
    {
        var cpp = ClientSource;
        Assert.DoesNotContain("wcsstr(", cpp);
        Assert.Contains("CommandLineToArgvW(GetCommandLineW(), &argc)", cpp);
        Assert.Contains("if (IsChildProcess(argc, argv))", cpp);
        Assert.Contains("if (wcscmp(a, L\"--\") == 0) return false;", cpp);
        Assert.Contains("_wcsnicmp(a, L\"type=\", 5) == 0 && a[5] != L'\\0'", cpp);
    }

    // Sem .NET (hostfxr em falta, erro do host) o Éteris avisa com o caminho do registo, menos nos ganchos do Velopack
    // (sem janela, com prazo).
    [Fact]
    public void Client_ReportsDotNetStartupFailures()
    {
        var cpp = ClientSource;
        Assert.Contains("MessageBoxW(nullptr, text.c_str()", cpp);
        Assert.Contains("if (IsVelopackHook(argc, argv)) return;", cpp);
        Assert.Equal(2, Regex.Matches(cpp, @"ReportStartupFailure\(argc, argv, ").Count);
    }

    // A versão da CEF da distribuição fica gravada na DLL (recurso de versão), para a publicação a comparar com o
    // manifest.json do runtime (build/sandbox/verify-client.ps1).
    [Fact]
    public void Client_CarriesTheCefVersionItWasBuiltWith()
    {
        var cmake = Read("build", "sandbox", "client", "CMakeLists.txt");
        Assert.Contains("\"${CEF_ROOT}/include/cef_version.h\"", cmake);
        Assert.Contains("configure_file(eteris_client.rc.in", cmake);
        var rc = Read("build", "sandbox", "client", "eteris_client.rc.in");
        Assert.Contains("VALUE \"ProductVersion\", \"@CEF_VERSION@\"", rc);
        Assert.Contains("VALUE \"Comments\", \"cef_commit=@CEF_COMMIT_HASH@\"", rc);
        Assert.Contains("Chromium Embedded Framework Authors", rc);
        var verify = Read("build", "sandbox", "verify-client.ps1");
        Assert.Contains("cef_commit=([0-9a-f]{40})", verify);
        Assert.Contains("$manifest.cef.commit", verify);
        Assert.Contains("'KERNEL32.DLL'", verify);
        Assert.Contains("'EterisSandboxInfo,RunWinMain'", verify);
        Assert.Contains("CEF_INSTALLER_CONFIG", verify);
    }

    // A DLL é carregada em todos os filhos com sandbox: nada corre ao carregá-la, e o .NET só no processo principal.
    [Fact]
    public void Client_DoesNothingAtLoadAndOnlyTheBrowserProcessStartsDotNet()
    {
        var cpp = ClientSource;
        Assert.DoesNotMatch(@"\bDllMain\s*\(", cpp);
        Assert.Contains("\"hostfxr_main_startupinfo\"", cpp);
        Assert.Single(Regex.Matches(cpp, @"LoadLibraryExW\("));
        var cmake = Read("build", "sandbox", "client", "CMakeLists.txt");
        // Os /DELAYLOAD e a CRT estática (/MT) vêm das macros da CEF: não os trocar.
        Assert.Contains("SET_LIBRARY_TARGET_PROPERTIES(eteris_client)", cmake);
        Assert.DoesNotContain("CEF_RUNTIME_LIBRARY_FLAG", cmake);
        Assert.DoesNotContain("/MD", cmake);
    }

    // Patch v2: com WindowsSandboxInfo, o CefSharp não exige o processo filho .NET e limpa o caminho dele (a CEF
    // desliga o sandbox sempre que browser_subprocess_path está preenchido).
    [Fact]
    public void CefSharpPatch_EnablesTheSandboxOnlyWithTheBootstrapPointer()
    {
        var patch = Read("build", "cefsharp", "windows-sandbox-info.patch");
        foreach (var file in new[]
                 {
                     "CefSharp.Core.Runtime.RefAssembly/CefSharp.Core.Runtime.netcore.cs", "CefSharp.Core.Runtime/Cef.h",
                     "CefSharp.Core.Runtime/CefSettingsBase.h", "CefSharp.Core/CefSettingsBase.cs",
                 })
            Assert.Contains("diff --git a/" + file + " b/" + file, patch);
        Assert.Contains("+                const bool useWindowsSandbox = cefSettings->WindowsSandboxInfo != IntPtr::Zero;", patch);
        Assert.Contains("+                    cef_string_clear(&settings.browser_subprocess_path);", patch);
        Assert.Contains("cefSettings->WindowsSandboxInfo.ToPointer()", patch);
        Assert.Contains("+                    _cefSettings->no_sandbox = value == IntPtr::Zero;", patch);
        Assert.DoesNotContain("\uFEFF", patch, StringComparison.Ordinal); // sem BOM a meio (o v1 tinha um)
    }
}
