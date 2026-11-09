using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// D9 de docs/sandbox.md: o serviço de rede só vai para o AppContainer (NetworkServiceSandbox) com as páginas em sandbox
// e a ACL da pasta confirmada; sem ela o Chromium falhava o lançamento e marcava o perfil sem sandbox de rede até à
// versão principal seguinte do motor.
public class NetworkSandboxTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void OnlyWithThePageSandboxAndTheAcl(bool pageSandbox, bool acl, bool expected)
        => Assert.Equal(expected, NetworkSandbox.ShouldEnable(pageSandbox, () => acl));

    // Sem o sandbox das páginas a ACL nem chega a ser lida.
    [Fact]
    public void WithoutThePageSandbox_TheAclIsNotRead()
        => Assert.False(NetworkSandbox.ShouldEnable(false, () => throw new InvalidOperationException("lida")));

    // Os SIDs têm de ser os mesmos do script PowerShell (cópias à mão e a prova do D9).
    [Fact]
    public void Sids_MatchTheScript()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "build", "sandbox", "network-sandbox-acl.ps1"));
        Assert.Equal(2, NetworkSandbox.CapabilitySids.Count);
        Assert.All(NetworkSandbox.CapabilitySids, sid => Assert.Contains("'" + sid + "'", script));
    }

    [Fact]
    public void InstallRoot_IsTheVelopackRootOnlyWithUpdateExe()
    {
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            Assert.Equal(current, NetworkSandbox.InstallRoot(current + Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(root, "Update.exe"), "");
            Assert.Equal(root, NetworkSandbox.InstallRoot(current + Path.DirectorySeparatorChar));
            Assert.Equal(root, NetworkSandbox.InstallRoot(current));
            var other = Directory.CreateDirectory(Path.Combine(root, "app-0.2.9")).FullName;
            Assert.Equal(other, NetworkSandbox.InstallRoot(other));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Numa pasta temporária do utilizador: dar a ACL à pasta chega ao exe que está dentro (herança), como numa instalação.
    [Fact]
    public void Grant_IsInheritedByTheExecutable()
    {
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            var exe = Path.Combine(current, "Eteris.Browser.exe");
            File.WriteAllText(exe, "");
            Assert.False(NetworkSandbox.Granted(exe));
            Assert.False(NetworkSandbox.Decide(true, exe));

            NetworkSandbox.Grant(root);
            Assert.True(NetworkSandbox.Granted(root));
            Assert.True(NetworkSandbox.Granted(current));
            Assert.True(NetworkSandbox.Granted(exe));
            Assert.True(NetworkSandbox.Decide(true, exe));
            Assert.True(NetworkSandbox.Enabled);
            Assert.False(NetworkSandbox.Decide(false, exe));
            Assert.False(NetworkSandbox.Enabled);

            // Uma pasta criada depois (a versão seguinte do Velopack) também herda.
            var next = Directory.CreateDirectory(Path.Combine(root, "next")).FullName;
            File.WriteAllText(Path.Combine(next, "Eteris.Browser.exe"), "");
            Assert.True(NetworkSandbox.Granted(Path.Combine(next, "Eteris.Browser.exe")));

            // Uma entrada a negar ganha.
            var info = new FileInfo(exe);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(NetworkSandbox.CapabilitySids[1]),
                FileSystemRights.ReadAndExecute, AccessControlType.Deny));
            info.SetAccessControl(security);
            Assert.False(NetworkSandbox.Granted(exe));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Granted_IsFalseForAMissingPath()
        => Assert.False(NetworkSandbox.Granted(Path.Combine(Path.GetTempPath(), "eteris-nao-existe", "Eteris.Browser.exe")));

    [Fact]
    public void Build_AddsTheFeatureOnlyWhenAsked()
    {
        Assert.DoesNotContain(NetworkSandbox.Feature, Features(CefConfigurator.Build("p")));
        var on = Features(CefConfigurator.Build("p", networkSandbox: true));
        Assert.Contains(NetworkSandbox.Feature, on);
        // As funcionalidades do EngineHardening continuam lá.
        Assert.Contains("ThirdPartyStoragePartitioning", on);
    }

    // Os ganchos de instalação e atualização só mexem em alguma coisa nas publicações com sandbox: CefSharp do Éteris E
    // arranque pelo bootstrap. O processo dos testes nunca arranca pelo bootstrap, com o CefSharp oficial ou com o do feed
    // (-p:EterisCefSharpFeed): os ganchos ficam inertes e não dão a ACL à pasta dos testes. O de desinstalação corre em
    // qualquer publicação, mas só dentro de uma instalação do Velopack, que o processo dos testes não é.
    [Fact]
    public void Hooks_AreWiredAndInertOutsideTheBootstrap()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Program.cs"));
        Assert.Contains(".OnAfterInstallFastCallback(_ => NetworkSandbox.OnInstalled())", program);
        Assert.Contains(".OnAfterUpdateFastCallback(_ => NetworkSandbox.OnInstalled())", program);
        Assert.Contains(".OnBeforeUninstallFastCallback(_ => NetworkSandbox.OnUninstalling())", program);
        Assert.False(SandboxInfo.LaunchedByBootstrap);
        Assert.False(NetworkSandbox.Granted(AppContext.BaseDirectory));
        NetworkSandbox.OnInstalled();
        NetworkSandbox.OnUninstalling();
        Assert.False(NetworkSandbox.Granted(AppContext.BaseDirectory));
        Assert.False(NetworkSandbox.OnInstalled(false, AppContext.BaseDirectory));
        Assert.Empty(NetworkSandbox.OnUninstalling(Environment.ProcessPath, _ => throw new InvalidOperationException("apagou")));
    }

    // Uma versão SEM sandbox (publicação de emergência com -NoSandbox, CefSharp oficial) instalada por cima de uma com
    // sandbox tem de apagar, ao desinstalar, os perfis que essa deixou: o gancho de desinstalação não depende do sandbox.
    [Fact]
    public void Uninstall_DoesNotDependOnTheSandbox()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "NetworkSandbox.cs"));
        Assert.Contains("public static void OnUninstalling() => OnUninstalling(Environment.ProcessPath, DeleteAppContainerProfile);", source);
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            var exe = Path.Combine(current, "Eteris.Browser.exe");
            File.WriteAllText(exe, "");
            // Fora do Velopack (sem Update.exe): nada é apagado.
            Assert.Empty(NetworkSandbox.OnUninstalling(exe, _ => throw new InvalidOperationException("apagou")));
            // Instalação do Velopack com o ficheiro que a versão com sandbox deixou na raiz.
            File.WriteAllText(Path.Combine(root, "Update.exe"), "");
            var left = NetworkSandbox.ProfileName(@"C:\Users\Outro\AppData\Local\EterisBrowser\current\Eteris.Browser.exe");
            File.WriteAllText(Path.Combine(root, NetworkSandbox.ProfilesFile), left + "\r\n");
            var deleted = new List<string>();
            NetworkSandbox.OnUninstalling(exe, name => { deleted.Add(name); return 0; });
            Assert.Contains(left, deleted);
            Assert.Contains(NetworkSandbox.ProfileName(exe), deleted);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // O ramo com sandbox do gancho de instalação/atualização, numa instalação do Velopack de mentira: a ACL vai para a
    // raiz e chega ao exe da versão.
    [Fact]
    public void OnInstalled_GrantsTheVelopackRoot()
    {
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            File.WriteAllText(Path.Combine(root, "Update.exe"), "");
            var exe = Path.Combine(current, "Eteris.Browser.exe");
            File.WriteAllText(exe, "");
            Assert.True(NetworkSandbox.OnInstalled(true, current));
            Assert.True(NetworkSandbox.Granted(root));
            Assert.True(NetworkSandbox.GrantedForLaunch(exe));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Como o Chromium (GetAppContainerProfileName): «cr.sb.net» + SHA1 do caminho em UTF-8, maiúsculas. Caminho fictício
    // com um carácter fora do ASCII («á»: UTF-8, UTF-16 e ANSI dão bytes diferentes); o valor esperado foi calculado fora
    // do Éteris (SHA1 dos bytes UTF-8, em Python e no PowerShell). A fórmula foi conferida com um perfil real criado pelo
    // Chromium na prova do Velopack de 2026-11-08 (docs/sandbox.md).
    [Fact]
    public void ProfileName_IsTheChromiumOne()
    {
        Assert.Equal("cr.sb.net5B29C3D47D470C8D99DC5CDBA6858F55CEBD46A3",
            NetworkSandbox.ProfileName(@"C:\Users\Usuário\AppData\Local\EterisBrowser\current\Eteris.Browser.exe"));
        Assert.Equal(49, NetworkSandbox.ProfileName(@"C:\a.exe").Length); // o Chromium corta aos 50
        Assert.NotEqual(NetworkSandbox.ProfileName(@"C:\Eteris\Eteris.Browser.exe"), NetworkSandbox.ProfileName(@"c:\eteris\eteris.browser.exe"));
    }

    // Desinstalação: os perfis guardados no arranque (um por caminho com que o exe foi arrancado) e o do próprio caminho
    // do gancho são apagados; fora do Velopack nada é guardado.
    [Fact]
    public void Uninstall_RemovesTheRememberedProfiles()
    {
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            var exe = Path.Combine(current, "Eteris.Browser.exe");
            File.WriteAllText(exe, "");

            // Fora do Velopack (sem Update.exe): nada fica escrito.
            NetworkSandbox.Remember(exe);
            Assert.False(File.Exists(Path.Combine(current, NetworkSandbox.ProfilesFile)));

            File.WriteAllText(Path.Combine(root, "Update.exe"), "");
            var file = Path.Combine(root, NetworkSandbox.ProfilesFile);
            NetworkSandbox.Remember(exe);
            NetworkSandbox.Remember(exe);
            var other = exe.ToUpperInvariant(); // o mesmo exe arrancado por outra grafia do caminho
            NetworkSandbox.Remember(other);
            File.AppendAllText(file, "lixo\r\ncr.sb.cdm0000000000000000000000000000000000000000\r\n");
            Assert.Equal(new[] { NetworkSandbox.ProfileName(exe), NetworkSandbox.ProfileName(other) },
                File.ReadAllLines(file).Where(l => l.StartsWith("cr.sb.net", StringComparison.Ordinal)));

            var deleted = new List<string>();
            var names = NetworkSandbox.OnUninstalling(exe, name => { deleted.Add(name); return 0; });
            Assert.Equal(names, deleted);
            Assert.Contains(NetworkSandbox.ProfileName(exe), deleted);
            Assert.Contains(NetworkSandbox.ProfileName(other), deleted);
            Assert.All(deleted, name => Assert.Matches("^cr\\.sb\\.net[0-9A-F]{40}$", name));
            Assert.Equal(deleted.Count, deleted.Distinct().Count());

            // Um erro a apagar um perfil não impede os outros nem a desinstalação.
            var tried = 0;
            NetworkSandbox.OnUninstalling(exe, _ => { tried++; throw new ExternalException("falhou"); });
            Assert.Equal(names.Count, tried);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // «ALL APPLICATION PACKAGES» também abre a pasta ao AppContainer do serviço de rede (não é LPAC): por exemplo numa
    // instalação em Program Files. E a ACL tem de chegar ao libcef.dll e à DLL cliente, não só ao exe.
    [Fact]
    public void Granted_AcceptsAllApplicationPackagesAndChecksTheDlls()
    {
        var root = Directory.CreateTempSubdirectory("eteris-netsbx-").FullName;
        try
        {
            var exe = Path.Combine(root, "Eteris.Browser.exe");
            File.WriteAllText(exe, "");
            var info = new DirectoryInfo(root);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(NetworkSandbox.AllApplicationPackagesSid),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            Assert.True(NetworkSandbox.Granted(exe));
            Assert.True(NetworkSandbox.GrantedForLaunch(exe));

            // Uma DLL sem herança (atualização interrompida a meio): o lançamento falharia, por isso não se liga.
            foreach (var dll in new[] { "libcef.dll", "Eteris.Browser.dll" })
            {
                var path = Path.Combine(root, dll);
                File.WriteAllText(path, "");
                Assert.True(NetworkSandbox.GrantedForLaunch(exe));
                var file = new FileInfo(path);
                var own = file.GetAccessControl();
                own.SetAccessRuleProtection(true, false);
                own.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                file.SetAccessControl(own);
                Assert.True(NetworkSandbox.Granted(exe));
                Assert.False(NetworkSandbox.GrantedForLaunch(exe));
                Assert.False(NetworkSandbox.Decide(true, exe));
                File.Delete(path);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string[] Features(CefSharp.CefSettingsBase settings)
        => settings.CefCommandLineArgs["enable-features"].Split(',');

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
