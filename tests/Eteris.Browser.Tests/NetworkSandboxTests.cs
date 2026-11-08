using System.IO;
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

    // Os ganchos do Velopack só mexem na ACL nas publicações com sandbox (os testes usam o CefSharp oficial).
    [Fact]
    public void Hooks_AreWiredAndInertWithoutTheSandbox()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Program.cs"));
        Assert.Contains(".OnAfterInstallFastCallback(_ => NetworkSandbox.OnInstalled())", program);
        Assert.Contains(".OnAfterUpdateFastCallback(_ => NetworkSandbox.OnInstalled())", program);
        Assert.False(CefConfigurator.SandboxSupported);
        NetworkSandbox.OnInstalled();
        Assert.False(NetworkSandbox.Granted(AppContext.BaseDirectory));
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
