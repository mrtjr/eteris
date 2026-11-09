namespace Eteris.Browser.Infrastructure;

// E16 do plano de desempenho da 0.2.14 (ajuste e16, desligado à espera da decisão D-8): a poupança de energia do próprio
// Chromium. A camada chrome/ corre na CEF 156 (chrome_content_browser_client_cef.cc cria as partes principais do Chrome
// e os nós de página do Performance Manager), por isso o BatterySaverModeManager deve existir; com o modo ativo ele
// limita todos os compositores a 30 fps (battery_saver_mode_manager.cc:76-77, StartThrottlingAllFrameSinks(30 Hz)),
// alinha os despertares e desliga o pré-carregamento. NUNCA FOI PROVADO NA CEF: é o passo A do plano.
//
// O estado é a preferência global performance_tuning.battery_saver_mode.state
// (components/performance_manager/public/user_tuning/prefs.h:53-61): 0 desligado, 1 «abaixo do limiar» (com bateria
// baixa; o valor por omissão do Chromium e, portanto, o que o Éteris sempre teve), 2 «na bateria» (descontinuado no
// M121, lido como 1), 3 sempre ligado (mesmo na tomada). Vive no Local State, que o Éteris já escreve antes de o motor
// arrancar (o DNS cifrado): vai na mesma escrita (LocalState.Apply).
//
// Passo A (só num perfil de teste, depois da 0.2.13): o ajuste e16 com ETERIS_TEST_ENERGIA=3 e com =0, na
// tomada; fps.html tem de mostrar 30 fps no título com 3 e 60 com 0 (build/perf/README.md, «Fase 2»). Se cair para 30:
// a definição «Poupar energia na bateria» (desligada / com bateria baixa / sempre na bateria) e o padrão vêm com a D-8.
// Mudar em tempo real (SystemEvents.PowerModeChanged → estado 3 na bateria, 0 na tomada) precisava do gestor de
// preferências globais da CEF (CefPreferenceManager::GetGlobalPreferenceManager), que o CefSharp 156 não expõe: só o
// IRequestContext.SetPreference, que é do perfil e não do Local State. Sem isso, «sempre na bateria» só se pode
// aproximar no arranque. Se o passo A falhar, a ideia cai e fica o P5 (patch na CEF, fase 3).
//
// Recuo: ETERIS_TEST_ENERGIA=0 (estado 0), ou o ajuste desligado (--eteris-sem-ajustes, --eteris-sem-ajustes-motor),
// que tira a chave e devolve o valor do Chromium.
public static class EnergySaver
{
    public static readonly IReadOnlyList<string> PrefPath = ["performance_tuning", "battery_saver_mode", "state"];

    public const int Off = 0;
    public const int LowBattery = 1;
    public const int Always = 3;

    // O estado a escrever neste arranque: nulo sem o ajuste (nada escrito; uma chave antiga sai); com ele, o do
    // ETERIS_TEST_ENERGIA (só fora de uma instalação) ou, sem a variável, «com bateria baixa» (a proposta da D-8, como o
    // Chrome).
    public static int? StateFor(bool tweakOn, int? testState) => tweakOn ? testState ?? LowBattery : null;

    // Sem o ajuste nem sequer se lê a variável.
    public static int? StateForStartup() => PerfTuning.IsOn(PerfTuning.EnergySaver) ? StateFor(true, PerfDiagnostics.EnergyState) : null;
}
