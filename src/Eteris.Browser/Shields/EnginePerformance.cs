using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Shields;

// Funcionalidades do motor para o desempenho (plano de desempenho da 0.2.14), ao lado das promessas de
// Shields/EngineHardening.cs: entram pelo mesmo Merge (nunca substituem o enable-features) e cada uma fica atrás do seu
// ajuste (Infrastructure/PerfTuning.cs), desligável sem compilar de novo; --eteris-sem-ajustes desliga a lista inteira.
// Como nas listas de privacidade, cada nome tem de ser uma base::Feature do libcef.dll em uso (EngineFeatureNamesTests,
// sem os parâmetros «:nome/valor»).
public static class EnginePerformance
{
    public static readonly IReadOnlyList<(string Feature, string What, string Tweak)> Enabled =
    [
        // E7: cada página guardada para Voltar/Avançar instantâneo mantém o renderer dela vivo até 10 min; o Chromium
        // guarda 6 por aba (back_forward_cache_impl.cc). Com 3, Voltar 1–3 passos continua instantâneo.
        ("BackForwardCacheSize:cache_size/3", "Voltar/Avançar instantâneo para as 3 últimas páginas de cada aba (o Chromium guarda 6)",
            PerfTuning.SmallBackForwardCache),
    ];

    // «Nome:param/valor/…» → «Nome».
    public static string FeatureName(string entry) => entry.Split(':', 2)[0];

    // Os parâmetros de uma entrada, por nome.
    public static IReadOnlyDictionary<string, string> Parameters(string entry)
    {
        var parts = entry.Split(':', 2);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts.Length < 2) return result;
        var values = parts[1].Split('/');
        for (var i = 0; i + 1 < values.Length; i += 2) result[values[i]] = values[i + 1];
        return result;
    }

    // Junta à linha de comandos do motor as funcionalidades com o ajuste ligado (EngineHardening.Apply).
    public static void Apply(IDictionary<string, string> args, Func<string, bool> isOn)
    {
        var on = Enabled.Where(f => isOn(f.Tweak)).Select(f => f.Feature).ToList();
        if (on.Count > 0) EngineHardening.Merge(args, "enable-features", on);
    }
}
