namespace Eteris.Browser.Shields;

// Uma funcionalidade do motor só de aspeto (proposta 3, «Pele nova»), ligada por uma opção das Definições.
//   Feature            o nome da base::Feature (EngineFeatureNamesTests confere-o no libcef.dll);
//   What               o que muda, para eteris://privacy/;
//   Disable            vai para o disable-features em vez do enable-features;
//   ChangesFingerprint muda o que os sites conseguem medir: nunca ligada por omissão e assinalada em eteris://privacy/
//                      (a mesma regra de Shields/EnginePerformance.cs).
public sealed record EngineLook(string Feature, string What, bool Disable = false, bool ChangesFingerprint = false);

// As páginas web ficam, por omissão, com as barras de deslocamento de um Chrome 156 no mesmo PC: a mesma largura, logo a
// mesma impressão digital. Nada aqui entra sem a opção do utilizador.
//
// Fora da lista, de propósito (3.3 (3)):
// - As barras Fluent (FluentScrollbar, FluentOverlayScrollbar) não existem no motor 156 (EngineFeatureNamesTests).
// - A RootScrollbarFollowsBrowserTheme vem desligada no motor (estado por omissão 0 no libcef.dll do NuGet e no da
//   compilação própria, EngineFeatureNamesTests): não é ela que pinta a barra da raiz de escuro numa página clara, e
//   desligá-la não mudava nada. A barra da raiz já segue o color-scheme da página; o que falta é dar ao motor o tema do
//   Éteris (o set_chrome_color_scheme da CEF), que não está no CefSharp do NuGet: fica para a compilação própria.
//
// Entra pelo mesmo EngineHardening.Merge (nunca substitui o enable-features) e aplica-se no arranque do motor.
public static class EngineAppearance
{
    // Barras sobrepostas: finas, por cima do conteúdo, aparecem ao rolar e ao entrar com o rato. A barra deixa de ocupar
    // largura (innerWidth − clientWidth passa a 0), e os sites medem isso: o Éteris deixa de se confundir com um Chrome
    // no mesmo Windows. O «flash» já vem ligado no motor; vai explícito para não depender disso.
    public static readonly IReadOnlyList<EngineLook> OverlayScrollbars =
    [
        new("OverlayScrollbar", "barras de deslocamento sobrepostas: a barra deixa de ocupar largura na página", ChangesFingerprint: true),
        new("OverlayScrollbarFlashWhenMouseEnter", "as barras sobrepostas aparecem ao entrar com o rato"),
    ];

    public static readonly IReadOnlyList<EngineLook> All = [.. OverlayScrollbars];

    // As entradas que valem com estas escolhas (a opção das Definições, lida no arranque: CefConfigurator.Prepare).
    public static IReadOnlyList<EngineLook> For(bool overlayScrollbars) => overlayScrollbars ? OverlayScrollbars : [];

    // Junta à linha de comandos do motor as entradas escolhidas (EngineHardening.Apply).
    public static void Apply(IDictionary<string, string> args, bool overlayScrollbars)
    {
        var on = For(overlayScrollbars);
        var enable = on.Where(f => !f.Disable).Select(f => f.Feature).ToList();
        var disable = on.Where(f => f.Disable).Select(f => f.Feature).ToList();
        if (enable.Count > 0) EngineHardening.Merge(args, "enable-features", enable);
        if (disable.Count > 0) EngineHardening.Merge(args, "disable-features", disable);
    }

    // As entradas em vigor na linha de comandos com que o motor arrancou (eteris://privacy/ lê o estado real).
    public static IReadOnlyList<EngineLook> ActiveIn(IReadOnlyDictionary<string, string> args)
    {
        var enabled = EngineHardening.Switch(args, "enable-features");
        var disabled = EngineHardening.Switch(args, "disable-features");
        return All.Where(f => (f.Disable ? disabled : enabled).Contains(f.Feature)).ToList();
    }
}
