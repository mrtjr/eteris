using System.Diagnostics;
using System.IO;
using Eteris.Sync.Google;
using Microsoft.Win32;

namespace Eteris.Browser.Sync;

// Abre o ecrã de consentimento da Google no navegador do SISTEMA. A Google bloqueia o início de sessão dentro de
// motores embutidos (a CEF do Éteris incluída) e o Éteris também não o quer cá dentro. Quando o navegador
// predefinido é o próprio Éteris, usa outro navegador instalado (Firefox, Chrome, Brave… pela lista do Windows);
// sem nenhum, devolve false e a página mostra o endereço para copiar.
internal sealed class SystemBrowser : IExternalBrowser
{
    // Preferência quando o predefinido é o Éteris (os outros vêm depois, pela ordem do Windows).
    private static readonly string[] Preferred = ["firefox", "chrome", "brave"];

    public bool TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !GoogleEndpoints.IsAllowedBrowserUrl(uri)) return false;
        try
        {
            if (!DefaultIsEteris())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return true;
            }
            if (OtherBrowser() is not { } exe) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { url } })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    // O programa que abre https:// para este utilizador (UserChoice → ProgId → comando) é o Éteris?
    internal static bool DefaultIsEteris()
    {
        using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        var progId = choice?.GetValue("ProgId") as string;
        if (string.IsNullOrEmpty(progId)) return false;
        using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        return IsEteris(progId, command?.GetValue(null) as string);
    }

    internal static bool IsEteris(string? progId, string? command)
        => (progId ?? string.Empty).Contains("Eteris", StringComparison.OrdinalIgnoreCase)
           || (command ?? string.Empty).Contains("Eteris.Browser.exe", StringComparison.OrdinalIgnoreCase)
           || (command ?? string.Empty).Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);

    // Navegadores registados no Windows (StartMenuInternet), sem o Éteris.
    internal static string? OtherBrowser()
    {
        var found = new List<(string Name, string Exe)>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
            if (clients is null) continue;
            foreach (var name in clients.GetSubKeyNames())
            {
                using var command = clients.OpenSubKey($@"{name}\shell\open\command");
                if (Executable(command?.GetValue(null) as string) is not { } exe || IsEteris(name, exe)) continue;
                found.Add((name, exe));
            }
        }
        return Choose(found);
    }

    internal static string? Choose(IReadOnlyList<(string Name, string Exe)> browsers)
    {
        foreach (var wanted in Preferred)
            if (browsers.FirstOrDefault(b => b.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(b.Exe).Contains(wanted, StringComparison.OrdinalIgnoreCase)) is { Exe: { } exe })
                return exe;
        return browsers.FirstOrDefault().Exe;
    }

    internal static string? Executable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        var close = text.StartsWith('"') ? text.IndexOf('"', 1) : -1;
        if (text.StartsWith('"') && close < 0) return null;
        var exe = text.StartsWith('"') ? text[1..close] : text.Split(' ')[0];
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null;
    }
}
