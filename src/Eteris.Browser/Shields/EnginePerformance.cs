using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Shields;

// Uma funcionalidade do motor ligada (ou desligada) por um ajuste de desempenho.
//   Feature   «Nome» ou «Nome:param/valor/…» (a sintaxe do --enable-features do Chromium);
//   Tweak     o ajuste que a liga (Infrastructure/PerfTuning.cs);
//   Disable   vai para o disable-features em vez do enable-features;
//   Requires  só com este outro ajuste também ligado (a cache persistente da GPU sem a cache no disco, e3, não entra);
//   Unless    só com este outro ajuste desligado (a guarda do Graphite sem a cache no disco);
//   ChangesFingerprint  muda o que os sites conseguem medir (o resultado do canvas): nunca ligada por omissão sem a
//             decisão do dono e o workflow do farbling, e assinalada em eteris://privacy/;
//   Gpu       corre no processo da GPU: uma queda dele antes da 1.ª página conta hoje como falha do sandbox (D-18).
public sealed record EngineTweak(string Feature, string What, string Tweak, bool Disable = false, string? Requires = null,
                                 string? Unless = null, bool ChangesFingerprint = false, bool Gpu = false);

// Funcionalidades do motor para o desempenho (plano de desempenho da 0.2.14, fase 2: E7, E14 e E15), ao lado das
// promessas de Shields/EngineHardening.cs: entram pelo mesmo Merge (nunca substituem o enable-features) e cada uma fica
// atrás do seu ajuste (Infrastructure/PerfTuning.cs), desligável sem compilar de novo; --eteris-sem-ajustes-motor desliga
// a lista inteira (e o --eteris-sem-ajustes, tudo). Como nas listas de privacidade, cada nome tem de ser uma base::Feature
// do libcef.dll em uso (EngineFeatureNamesTests, sem os parâmetros «:nome/valor»); os «V8Flag_x» não são base::Feature
// (o gin passa-os ao V8 como --x) e o teste procura a flag do V8.
//
// Porquê: a nossa CEF compila com is_official_build, que força disable_fieldtrial_testing_config (cef/tools/gn_args.py):
// tudo o que o Chrome estável liga por variações (Finch) fica desligado no Éteris. As entradas vêm da configuração de
// testes das variações da árvore 156 (testing/variations/fieldtrial_testing_config.json, estudos para Windows), com os
// parâmetros dela; o D3 (o que um Chrome 156 em perfil temporário tem mesmo ligado) diz quais entram primeiro.
//
// Como medir cada uma (build/perf/README.md, «Fase 2»): um braço com só esse ajuste ligado contra L1, o B-CPU
// completo (fps, scroll, dano-zero, links, iframes) ou o B-MEM com o D9, chrome://gpu para confirmar o estado, e as provas
// de não regressão do plano (T1/T6 sem piscar, miniaturas, ecrã dividido, vídeo). Critério do E14: −≥ 0,5 p.p. na soma,
// sem regressão de fps, 0 fotogramas errados e 0 falhas da GPU num dia de uso. Do E15: −≥ 1 MB por renderer ou −≥ 4 MB de
// GPU por aba WebGL escondida.
public static class EnginePerformance
{
    public static readonly IReadOnlyList<EngineTweak> Enabled =
    [
        // E7: cada página guardada para Voltar/Avançar instantâneo mantém o renderer dela vivo até 10 min; o Chromium
        // guarda 6 por aba (back_forward_cache_impl.cc). Com 3, Voltar 1–3 passos continua instantâneo.
        new("BackForwardCacheSize:cache_size/3", "Voltar/Avançar instantâneo para as 3 últimas páginas de cada aba (o Chromium guarda 6)",
            PerfTuning.SmallBackForwardCache),

        // E14.1 — cc/base/features.cc:151-176 (desligada; limiares 360 e 60, fatores 2); lógica em
        // cc/trees/proxy_common.cc. Estudo ThrottleConsecutiveNoDamageMainFrames (Windows, 360/60 = os valores do código).
        // Faz: depois de 360 fotogramas seguidos em que a página pede um fotograma e não muda nada, o agendador do cc
        // espaça os BeginMainFrame (×2), e mais 60 depois (×2 outra vez); um fotograma com dano volta logo à cadência
        // normal. Risco: a cadência do requestAnimationFrame dessa página desce (observável por ela, como no Chrome com o
        // estudo); um contador feito por rAF que não desenha atrasa-se. Medir: dano-zero.html (deve cair muito) e
        // fps.html/scroll.html (não devem mudar); o renderer da aba é o processo afetado.
        new("ThrottleRepeatedNoDamageFrames", "abranda os fotogramas de uma página que pede animação sem mudar nada",
            PerfTuning.NoDamageThrottle),

        // E14.2 — components/viz/service/frame_sinks/external_begin_frame_source_win.cc:17-18,34-41 (desligada). Estudo
        // ExternalBeginFrameSourceWinUsesRunOrPostTask (Windows). Faz: o sinal do vsync corre o BeginFrame logo na thread
        // do Viz (RunOrPostTask) em vez de o pôr na fila (PostTask). Risco: baixo; a ordem das tarefas do Viz muda.
        // Medir: a latência (entrada.html, B-ENTRADA: «quadro»), não a CPU, que deve ficar igual (revisão R1).
        new("ExternalBeginFrameSourceWinUsesRunOrPostTask", "o vsync começa o fotograma sem um salto de tarefa",
            PerfTuning.BeginFrameRunOrPost, Gpu: true),

        // E14.3 — components/viz/common/features.cc:344-345 (desligada). Estudo SingleVideoFrameRateThrottling (Windows).
        // Faz: quando a única coisa a mudar no ecrã é um vídeo de 30 fps, o Viz compõe a 30 Hz. Risco: um elemento por
        // cima do vídeo que mude é dano novo e volta aos 60 Hz; vídeos de cadência irregular. Medir: vídeo de 30 fps
        // (B-VID) com a CPU da GPU e do renderer; fps.html não deve mudar.
        new("SingleVideoFrameRateThrottling", "uma página só com um vídeo de 30 fps compõe a 30 Hz",
            PerfTuning.SingleVideoThrottle, Gpu: true),

        // E14.4 — gpu/config/gpu_finch_features.cc:480-493 (GpuPersistentCache desligada só no Windows;
        // GpuPersistentCacheMetadata desligada, preload_count 50). Estudos GpuPersistentCache e GpuPersistentCacheMetadata
        // (Windows). Faz: os shaders do compositor e do Skia (GrShaderCache) passam para a cache persistente, numa pasta
        // GPUPersistentCache na raiz dos dados (chrome_content_browser_client.cc:5201-5206), aberta pelo processo do
        // navegador para o da GPU em sandbox. Precisa do e3: a fábrica destes ficheiros não olha para o
        // disable-gpu-shader-disk-cache (content/browser/gpu/gpu_process_host.cc:572-588), por isso só liga com a cache
        // no disco autorizada (D-4); apaga-se com as outras caches da GPU (ProfileCleanup.GlobalGpuCaches). Risco: dado
        // local (o que se viu, de forma fraca), como o E3. Medir: arranque e 1.º segundo de animação (webgl.html,
        // fps.html) com a cache quente; as pastas, com o e3-cache-gpu.ps1.
        new("GpuPersistentCache", "cache persistente dos shaders da GPU (com a cache no disco)",
            PerfTuning.GpuPersistentCache, Requires: PerfTuning.ShaderCache, Gpu: true),
        new("GpuPersistentCacheMetadata", "a cache persistente pré-carrega os 50 shaders mais usados",
            PerfTuning.GpuPersistentCache, Requires: PerfTuning.ShaderCache, Gpu: true),

        // E14.5 — components/viz/common/features.cc:116,124-140 (desligada; no Windows o modo por omissão é limit_to_ui).
        // Estudo DelegatedCompositingOnWindows. Faz: as superfícies deixam de se fundir na passagem raiz e vão como camadas
        // do DirectComposition. Revisão R1: no Éteris a raiz de cada aba só tem a página (display.cc:551-559,
        // surface_aggregator.cc:1841-1849), por isso espera-se 0 ou uma passagem a mais; opcional e por último. Risco:
        // leituras de volta (miniaturas por Page.captureScreenshot: o Viz desliga a delegação nesse fotograma,
        // overlay_processor_win.cc:380-385), piscar na troca de abas (T1/T6). Medir: B-CPU, a GPU 3D do processo da GPU e
        // o dwm.exe.
        new("DelegatedCompositing", "composição delegada parcial no DirectComposition",
            PerfTuning.DelegatedCompositing, Gpu: true),

        // E14.6 — gpu/config/gpu_finch_features.cc:383-389 (SkiaGraphite desligado fora do Mac), 605-606 (o Windows x64
        // aceita-o), 396-397 (nas placas Intel decide o SkiaGraphiteWinIntel, que não ligamos: como no Chrome). Estudo
        // SkiaGraphite (Windows): dawn_skip_validation=true, dawn_backend_validation=false e dawn_use_persistent_cache=true;
        // o último já não é lido na 156 (SkiaGraphiteFeatureParams, gpu_finch_features.h:78-120): a cache dos pipelines é o
        // SkiaGraphiteUsePersistentCache, ligado por omissão no Windows (gpu_finch_features.cc:442-448). Faz: o Skia
        // Graphite (Dawn sobre D3D11) rasteriza e compõe em vez do Ganesh. Risco: MUDA O RESULTADO DO CANVAS (o hash de
        // canvas passa a ser o de um Chrome com Graphite): decisão D-3 com o workflow do farbling; quedas e erros de
        // desenho com controladores (a máquina de testes tem NVIDIA, Radeon integrada e o Parsec). Medir: por último no
        // E14, B-CPU completo, MotionMark (B-BENCH), chrome://gpu («Graphite»), WebGL e impressão.
        new("SkiaGraphite:dawn_skip_validation/true/dawn_backend_validation/false", "o Skia Graphite desenha as páginas (muda o canvas)",
            PerfTuning.SkiaGraphite, ChangesFingerprint: true, Gpu: true),
        // Sem o e3, o Graphite não escreve os pipelines no disco (a mesma regra da D-4 para a cache dos shaders).
        new("SkiaGraphiteUsePersistentCache", "sem a cache no disco, o Graphite não guarda os pipelines",
            PerfTuning.SkiaGraphite, Disable: true, Unless: PerfTuning.ShaderCache, Gpu: true),

        // E15.1 — third_party/blink/common/features.cc:739-742 (as duas desligadas). Estudo WebGLMemoryExperiments
        // (Windows). Faz: um contexto WebGL de uma página escondida larga o back buffer (recria-o ao voltar: 1
        // fotograma), e um OffscreenCanvas sabe que a página está escondida. Risco: um fotograma em branco ao voltar a
        // uma aba WebGL (T1). Medir: webgl.html numa aba escondida com os contadores «GPU Process Memory» (Dedicated e
        // Shared) e a PMF do processo da GPU.
        new("WebGLDiscardBackBuffer", "o WebGL das abas escondidas larga o back buffer",
            PerfTuning.HiddenWebGlBuffers, Gpu: true),
        new("OffscreenCanvasPropagateVisibility", "o OffscreenCanvas sabe que a aba está escondida",
            PerfTuning.HiddenWebGlBuffers),

        // E15.2 e E15.3 — content/browser/renderer_host/spare_render_process_host_manager_impl.cc:35-49 (desligadas).
        // Estudo MultipleSpareRPHs (Windows), sem o resto dele: não ligamos mais renderers de reserva (count 2 ou 5), que
        // gastam memória; o Chromium 156 já mantém 1 (content/common/features.cc:535-541). Fazem: com pressão de memória
        // o motor fecha o renderer de reserva (e15.2); o limiar para o criar ou fechar passa de moderado a crítico
        // (e15.3). Risco: a navegação seguinte a uma pressão perde o renderer já arrancado. Medir: o sinal simulado do E13
        // é do Éteris e o Chromium não o vê; a pressão real só com a D-16. Sem ela, só a ausência de regressão (D9 e
        // B-ARR da 2.ª aba).
        new("KillSpareRenderOnMemoryPressure", "com pouca memória, o renderer de reserva fecha",
            PerfTuning.KillSpareOnPressure),
        new("SpareRPHUseCriticalMemoryPressure", "o renderer de reserva só cede à pressão de memória crítica",
            PerfTuning.SpareCriticalPressure),

        // E15.4 — base/allocator/partition_alloc_features.cc:312-385. O plano falava do PartitionAllocMemoryReclaimer, mas
        // ele já está ligado na 156 com o intervalo recomendado (partition_alloc_support.cc:275-283): os braços do estudo
        // PartitionAllocMemoryReclaimer (Windows; 4 ou 8 s) só diferem do código nisto, a ordenação dos blocos ativos ao
        // purgar (braço SortActiveSlotSpans; desligada no código). Risco: CPU a mais em cada purga. Medir: D9
        // (PartitionAlloc por renderer) em C3/C5; só se o D9 mostrar peso.
        new("PartitionAllocSortActiveSlotSpans", "o PartitionAlloc ordena os blocos ativos ao purgar",
            PerfTuning.SortActiveSlotSpans),

        // E15.5 — v8/src/flags/flag-definitions.h:2700-2720 (sqrt_allocation_limits=false; os fatores do estudo são os do
        // código: 80, 80, 20, 1.1, 4.0). Estudo V8SqrtAllocationLimits (Windows), que liga V8Flag_sqrt_allocation_limits:
        // o gin passa as funcionalidades «V8Flag_x» ao V8 como --x (gin/v8_initializer.cc:223-336). Faz: os limites da
        // geração velha e do heap global crescem pela raiz quadrada do tamanho. Risco: mais recolhas de lixo nas páginas
        // grandes (CPU por memória). Medir: D9 (V8 por renderer) e Speedometer (B-BENCH); só se o D9 mostrar peso.
        new("V8Flag_sqrt_allocation_limits", "os limites do heap do V8 crescem pela raiz quadrada",
            PerfTuning.V8SqrtLimits),
    ];

    public const string V8FlagPrefix = "V8Flag_";

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

    // As entradas que valem com estes ajustes ligados.
    public static IReadOnlyList<EngineTweak> For(Func<string, bool> isOn)
        => Enabled.Where(f => isOn(f.Tweak) && (f.Requires is null || isOn(f.Requires)) && (f.Unless is null || !isOn(f.Unless))).ToList();

    // Junta à linha de comandos do motor as funcionalidades com o ajuste ligado (EngineHardening.Apply).
    public static void Apply(IDictionary<string, string> args, Func<string, bool> isOn)
    {
        var on = For(isOn);
        var enable = on.Where(f => !f.Disable).Select(f => f.Feature).ToList();
        var disable = on.Where(f => f.Disable).Select(f => f.Feature).ToList();
        if (enable.Count > 0) EngineHardening.Merge(args, "enable-features", enable);
        if (disable.Count > 0) EngineHardening.Merge(args, "disable-features", disable);
    }

    // As entradas em vigor na linha de comandos com que o motor arrancou (eteris://privacy/ lê o estado real).
    public static IReadOnlyList<EngineTweak> ActiveIn(IReadOnlyDictionary<string, string> args)
    {
        var enabled = EngineHardening.Switch(args, "enable-features");
        var disabled = EngineHardening.Switch(args, "disable-features");
        return Enabled.Where(f => (f.Disable ? disabled : enabled).Contains(f.Feature)).ToList();
    }
}
