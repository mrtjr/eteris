using System.Text.RegularExpressions;
using CefSharp.DevTools;
using CefSharp.DevTools.Emulation;

namespace Eteris.Browser.Shields;

// Dicas do cliente de alta entropia reduzidas no próprio motor, por aba (Emulation.setUserAgentOverride com os
// metadados, quando a sessão DevTools do ScriptletInjector nasce). Antes eram reduzidas por um script na página
// (getHighEntropyValues), mas os workers continuavam a dar os valores reais — Windows «19.0.0» e Chromium
// «156.0.8078.80» contra «10.0.0» e «156.0.0.0» na página — e o CreepJS compara os dois lados (medido,
// docs/paridade.md #6). Os metadados da sessão valem para a página, os frames do mesmo processo, os workers dedicados
// que eles criam e os cabeçalhos Sec-CH-UA, sem nenhuma função trocada que um site possa detetar.
// As marcas são as que o Chromium gera para a versão (o mesmo GREASE e a mesma ordem,
// components/embedder_support/user_agent_utils.cc), com a versão completa reduzida a «<maior>.0.0.0»; a CEF não junta a
// marca «Google Chrome». Limite: service workers e shared workers não pertencem à aba e continuam com os reais.
public static class ReducedClientHints
{
    public const string PlatformVersion = "10.0.0";

    private static readonly string[] GreaseyChars = [" ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_"];
    private static readonly string[] GreasedVersions = ["8", "99", "24"];

    // A versão maior do Chromium no User-Agent («Chrome/156.0.0.0»), ou null.
    public static int? MajorOf(string? userAgent)
        => userAgent is not null && Regex.Match(userAgent, @"Chrome/(\d+)\.") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var major)
            ? major : null;

    // As marcas, pela ordem do Chromium: com a versão maior (brands) ou a completa reduzida (fullVersionList).
    public static IReadOnlyList<(string Brand, string Version)> Brands(int major, bool fullVersion)
    {
        var greaseyBrand = $"Not{GreaseyChars[major % GreaseyChars.Length]}A{GreaseyChars[(major + 1) % GreaseyChars.Length]}Brand";
        var greaseyVersion = GreasedVersions[major % GreasedVersions.Length];
        var list = new[]
        {
            (greaseyBrand, fullVersion ? greaseyVersion + ".0.0.0" : greaseyVersion),
            ("Chromium", fullVersion ? $"{major}.0.0.0" : major.ToString()),
        };
        // Com duas marcas, a posição de cada uma é (seed + i) % 2.
        var shuffled = new (string, string)[2];
        for (var i = 0; i < list.Length; i++) shuffled[(major + i) % 2] = list[i];
        return shuffled;
    }

    public static UserAgentMetadata Metadata(int major) => new()
    {
        Brands = Brands(major, fullVersion: false).Select(b => new UserAgentBrandVersion { Brand = b.Brand, Version = b.Version }).ToList(),
        FullVersionList = Brands(major, fullVersion: true).Select(b => new UserAgentBrandVersion { Brand = b.Brand, Version = b.Version }).ToList(),
        FullVersion = $"{major}.0.0.0",
        Platform = "Windows",
        PlatformVersion = PlatformVersion,
        Architecture = "x86",
        Model = string.Empty,
        Mobile = false,
        Bitness = "64",
        Wow64 = false,
        FormFactors = ["Desktop"],
    };

    // Na sessão de uma aba: o User-Agent fica o do motor (o reduzido do Chromium), só os metadados mudam.
    public static async Task Apply(DevToolsClient client)
    {
        var version = await Browser.DevToolsCalls.Run(() => client.Browser.GetVersionAsync());
        if (MajorOf(version.UserAgent) is not { } major) return;
        await Browser.DevToolsCalls.Run(() => client.Emulation.SetUserAgentOverrideAsync(version.UserAgent, null, null, Metadata(major)));
    }
}
