using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Infrastructure;

// Serviço de rede com sandbox (docs/sandbox.md, D9). Com a funcionalidade NetworkServiceSandbox o Chromium arranca o
// serviço de rede num AppContainer, que só lê o executável e as DLLs da pasta da aplicação se ela der leitura+execução às
// capacidades «chromeInstallFiles» e «lpacChromeInstallFiles» (o instalador do Chrome faz o mesmo:
// chrome/installer/setup/configure_app_container_sandbox.cc). Sem essa ACL o lançamento falha, o Chromium grava
// net.network_service_failed_launch_major_version no Local State e o perfil fica com o serviço de rede SEM sandbox até à
// versão principal seguinte do motor — por isso a funcionalidade só se liga com a ACL confirmada no arranque.
//   - Instalação e atualização (ganchos do Velopack, Program.Main): a ACL é dada à raiz da instalação, com herança; as
//     pastas de cada versão herdam-na. A pasta é do utilizador: não precisa de administrador.
//   - Arranque (CefConfigurator.Prepare): só com as páginas em sandbox e a ACL presente no executável e nas DLLs que o
//     AppContainer abre; senão o serviço de rede corre como sempre, sem sandbox, e nada parte.
//   - Desinstalação (gancho do Velopack): o Chromium cria um perfil de AppContainer por caminho do executável
//     («cr.sb.net» + SHA1 do caminho: pasta em %LocalAppData%\Packages e entrada em AppContainer\Mappings no registo do
//     utilizador), que sobreviveria à desinstalação; o gancho apaga-o (DeleteAppContainerProfile).
// O mesmo em PowerShell, para cópias à mão: build/sandbox/network-sandbox-acl.ps1 e build/sandbox/appcontainer-profiles.ps1.
public static class NetworkSandbox
{
    public const string Feature = "NetworkServiceSandbox";

    // SIDs das duas capacidades no Chromium 156 (os mesmos de build/sandbox/network-sandbox-acl.ps1).
    public static readonly IReadOnlyList<string> CapabilitySids =
    [
        "S-1-15-3-1024-3424233489-972189580-2057154623-747635277-1604371224-316187997-3786583170-1043257646", // chromeInstallFiles
        "S-1-15-3-1024-2302894289-466761758-1166120688-1039016420-2430351297-4240214049-4028510897-3317428798", // lpacChromeInstallFiles
    ];

    // «ALL APPLICATION PACKAGES»: dá leitura a todos os AppContainers que não são LPAC (as pastas do Windows e de
    // Program Files já a trazem). O serviço de rede do Chromium 156 não é LPAC (WinSboxNetworkServiceSandboxIsLPAC
    // desligada), por isso esta entrada também chega — por exemplo numa instalação em Program Files.
    public const string AllApplicationPackagesSid = "S-1-15-2-1";

    // Prefixo do perfil do AppContainer do serviço de rede (sandbox/policy/win/sandbox_win.cc,
    // GetAppContainerProfileName): «cr.sb.net» + SHA1 do caminho do executável dos processos filhos, em hexadecimal.
    public const string ProfilePrefix = "cr.sb.net";

    // Na raiz de uma instalação do Velopack: os perfis criados por esta instalação (um por caminho com que o exe foi
    // arrancado), para o gancho de desinstalação os apagar.
    public const string ProfilesFile = "network-sandbox-profiles.txt";

    private static readonly Regex ProfileNamePattern = new("^cr\\.sb\\.net[0-9A-F]{40}$", RegexOptions.CultureInvariant);

    // Ligado neste arranque (diagnóstico).
    public static bool Enabled { get; private set; }

    // Decisão: serviço de rede com sandbox só quando as páginas também estão em sandbox e a ACL está confirmada (a
    // verificação só corre nesse caso).
    public static bool ShouldEnable(bool pageSandbox, Func<bool> aclGranted) => pageSandbox && aclGranted();

    internal static bool Decide(bool pageSandbox, string? processPath)
        => Enabled = ShouldEnable(pageSandbox, () => processPath is not null && GrantedForLaunch(processPath));

    // O que o AppContainer abre logo: o executável (o Chromium verifica-o antes de lançar) e as DLLs carregadas no
    // arranque do filho — o libcef.dll e a DLL cliente do bootstrap. Se a herança só chegou a parte da pasta (uma
    // atualização interrompida a meio), o lançamento falharia e o perfil ficaria marcado sem sandbox de rede.
    public static bool GrantedForLaunch(string exe)
        => Granted(exe) && LaunchFiles(exe).All(Granted);

    private static IEnumerable<string> LaunchFiles(string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "";
        foreach (var file in new[] { Path.Combine(dir, "libcef.dll"), SandboxInfo.ClientDllPath(exe) })
            if (file is not null && File.Exists(file)) yield return file;
    }

    // Pasta que recebe a ACL: na instalação do Velopack (%LocalAppData%\<pacote>\current\Eteris.Browser.exe, com o
    // Update.exe na raiz) é a raiz, para as pastas das versões seguintes a herdarem; fora dela, a própria pasta.
    public static string InstallRoot(string appDir)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir));
        var parent = Path.GetDirectoryName(dir);
        return parent is not null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
               && File.Exists(Path.Combine(parent, "Update.exe"))
            ? parent
            : dir;
    }

    // O ficheiro ou a pasta dá leitura+execução ao AppContainer do serviço de rede — às duas capacidades, ou a «ALL
    // APPLICATION PACKAGES» —, por entradas próprias ou herdadas que se apliquem ao próprio objeto (não «só herdar»), e
    // nenhuma delas nega. Qualquer erro conta como não.
    public static bool Granted(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Where(r => (CapabilitySids.Contains(r.IdentityReference.Value) || r.IdentityReference.Value == AllApplicationPackagesSid)
                            && (r.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                .ToList();
            if (rules.Any(r => r.AccessControlType == AccessControlType.Deny && (r.FileSystemRights & FileSystemRights.ReadAndExecute) != 0))
                return false;
            bool Allows(string sid) => rules.Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Value == sid
                                                      && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
            return Allows(AllApplicationPackagesSid) || CapabilitySids.All(Allows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    // Leitura+execução herdada (pastas e ficheiros) para as duas capacidades. Só na pasta indicada.
    public static void Grant(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl(AccessControlSections.Access);
        foreach (var sid in CapabilitySids)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    // Nome do perfil do AppContainer para um caminho do executável, exatamente como o Chromium o calcula (o caminho tal
    // como o processo principal foi arrancado: a forma curta 8.3 ou outras maiúsculas dão outro perfil).
    public static string ProfileName(string exePath)
        => ProfilePrefix + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exePath)));

    // Arranque com o serviço de rede em sandbox: numa instalação do Velopack guarda o nome do perfil na raiz (só se
    // ainda lá não estiver), para a desinstalação o apagar mesmo que o exe tenha sido arrancado por outro caminho.
    public static void Remember(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (dir is null) return;
            var root = InstallRoot(dir);
            if (string.Equals(root, Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase)) return; // fora do Velopack
            var file = Path.Combine(root, ProfilesFile);
            var name = ProfileName(exePath);
            if (File.Exists(file) && File.ReadLines(file).Any(line => line.Trim() == name)) return;
            File.AppendAllText(file, name + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
        }
    }

    // Perfis desta instalação: os guardados na raiz e os do próprio caminho do exe (o gancho corre pelo caminho com que
    // o Update.exe o arranca), nas formas longa e curta.
    internal static IReadOnlyList<string> ProfilesOf(string exePath)
    {
        var names = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            var file = dir is null ? null : Path.Combine(InstallRoot(dir), ProfilesFile);
            if (file is not null && File.Exists(file))
                names.AddRange(File.ReadLines(file).Select(line => line.Trim()).Where(line => ProfileNamePattern.IsMatch(line)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
        }
        foreach (var path in new[] { exePath, PathForm(exePath, longForm: true), PathForm(exePath, longForm: false) })
            if (!string.IsNullOrEmpty(path)) names.Add(ProfileName(path));
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    // Os ganchos só mexem em alguma coisa nas publicações com sandbox: o CefSharp do Éteris e o arranque pelo bootstrap
    // (é o que o Update.exe arranca). Uma compilação com o CefSharp do feed mas sem o bootstrap não precisa da ACL.
    private static bool HooksApply => CefConfigurator.SandboxSupported && SandboxInfo.LaunchedByBootstrap;

    // Ganchos do Velopack depois de instalar e de atualizar (correm na versão nova, sem janela, com poucos segundos).
    // Uma falha não impede a instalação (o arranque verifica e, sem a ACL, o serviço de rede corre como sempre).
    public static void OnInstalled() => OnInstalled(HooksApply, AppContext.BaseDirectory);

    internal static bool OnInstalled(bool applies, string appDir)
    {
        if (!applies) return false;
        try
        {
            Grant(InstallRoot(appDir));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    // Gancho do Velopack antes de desinstalar: apaga os perfis de AppContainer desta instalação (pasta em
    // %LocalAppData%\Packages e entrada em AppContainer\Mappings). Um perfil que não existe é ignorado.
    public static void OnUninstalling() => OnUninstalling(HooksApply, Environment.ProcessPath, DeleteAppContainerProfile);

    internal static IReadOnlyList<string> OnUninstalling(bool applies, string? exePath, Func<string, int> delete)
    {
        if (!applies || string.IsNullOrEmpty(exePath)) return [];
        var names = ProfilesOf(exePath);
        foreach (var name in names)
        {
            try
            {
                delete(name);
            }
            catch (Exception e) when (e is SystemException)
            {
            }
        }
        return names;
    }

    private static string? PathForm(string path, bool longForm)
    {
        var buffer = new StringBuilder(1024);
        var n = longForm ? GetLongPathNameW(path, buffer, buffer.Capacity) : GetShortPathNameW(path, buffer, buffer.Capacity);
        return n > 0 && n < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetLongPathNameW(string path, StringBuilder buffer, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetShortPathNameW(string path, StringBuilder buffer, int length);
}
