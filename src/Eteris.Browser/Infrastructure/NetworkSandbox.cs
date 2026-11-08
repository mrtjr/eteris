using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Eteris.Browser.Infrastructure;

// Serviço de rede com sandbox (docs/sandbox.md, D9). Com a funcionalidade NetworkServiceSandbox o Chromium arranca o
// serviço de rede num AppContainer, que só lê o executável e as DLLs da pasta da aplicação se ela der leitura+execução às
// capacidades «chromeInstallFiles» e «lpacChromeInstallFiles» (o instalador do Chrome faz o mesmo:
// chrome/installer/setup/configure_app_container_sandbox.cc). Sem essa ACL o lançamento falha, o Chromium grava
// net.network_service_failed_launch_major_version no Local State e o perfil fica com o serviço de rede SEM sandbox até à
// versão principal seguinte do motor — por isso a funcionalidade só se liga com a ACL confirmada no arranque.
//   - Instalação e atualização (ganchos do Velopack, Program.Main): a ACL é dada à raiz da instalação, com herança; as
//     pastas de cada versão herdam-na. A pasta é do utilizador: não precisa de administrador.
//   - Arranque (CefConfigurator.Initialize): só com as páginas em sandbox e a ACL presente no executável; senão o
//     serviço de rede corre como sempre, sem sandbox, e nada parte.
// O mesmo em PowerShell, para cópias à mão: build/sandbox/network-sandbox-acl.ps1.
public static class NetworkSandbox
{
    public const string Feature = "NetworkServiceSandbox";

    // SIDs das duas capacidades no Chromium 156 (os mesmos de build/sandbox/network-sandbox-acl.ps1).
    public static readonly IReadOnlyList<string> CapabilitySids =
    [
        "S-1-15-3-1024-3424233489-972189580-2057154623-747635277-1604371224-316187997-3786583170-1043257646", // chromeInstallFiles
        "S-1-15-3-1024-2302894289-466761758-1166120688-1039016420-2430351297-4240214049-4028510897-3317428798", // lpacChromeInstallFiles
    ];

    // Ligado neste arranque (diagnóstico).
    public static bool Enabled { get; private set; }

    // Decisão: serviço de rede com sandbox só quando as páginas também estão em sandbox e a ACL está confirmada (a
    // verificação só corre nesse caso).
    public static bool ShouldEnable(bool pageSandbox, Func<bool> aclGranted) => pageSandbox && aclGranted();

    internal static bool Decide(bool pageSandbox, string? processPath)
        => Enabled = ShouldEnable(pageSandbox, () => processPath is not null && Granted(processPath));

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

    // O ficheiro (o executável) ou a pasta dá leitura+execução às duas capacidades — entradas próprias ou herdadas,
    // desde que se apliquem ao próprio objeto (não «só herdar») e sem nenhuma a negar. Qualquer erro conta como não.
    public static bool Granted(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Where(r => CapabilitySids.Contains(r.IdentityReference.Value) && (r.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                .ToList();
            return CapabilitySids.All(sid => rules.Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Value == sid
                                                            && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute))
                   && !rules.Any(r => r.AccessControlType == AccessControlType.Deny && (r.FileSystemRights & FileSystemRights.ReadAndExecute) != 0);
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

    // Ganchos do Velopack depois de instalar e de atualizar (correm na versão nova, sem janela, com poucos segundos).
    // Só nas publicações com sandbox; uma falha não impede a instalação (o arranque verifica e, sem a ACL, o serviço de
    // rede corre como sempre).
    public static void OnInstalled()
    {
        if (!CefConfigurator.SandboxSupported) return;
        try
        {
            Grant(InstallRoot(AppContext.BaseDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
        }
    }
}
