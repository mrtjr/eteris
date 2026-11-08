namespace Eteris.Browser.Browser;

// Troca de página sem intervalo vazio («dupla memória»), como o Chrome faz ao trocar de aba: a página que sai fica à
// vista POR CIMA da que entra até esta ter apresentado um fotograma, e só então se esconde.
//
// Porquê: a janela de uma aba escondida é minimizada (as abas escondidas não gastam CPU, docs/paridade.md #1b) e o
// CefSharp encolhe-a para 0×0. Ao mostrá-la, o Chromium tem de voltar a dispor e rasterizar a página e, até lá, a
// janela apresenta um fotograma vazio — medido a 60 fps: 1–2 fotogramas brancos ou do Mica em cada troca, ~10 ao abrir
// uma aba nova e até 20 (~340 ms de branco) numa página pesada como a Wikipédia. Esconder primeiro e mostrar depois
// deixava ver isso tudo; com a página antiga por cima, o utilizador vê a antiga até a nova estar pronta.
public static class PageSwap
{
    public enum Step
    {
        Show,
        Hide,
        // Sai, mas continua à vista (por cima) até a página que entra apresentar um fotograma.
        Hold,
        // Quente (Tabs/WarmTabs.cs) e continua fora de vista: fica como está, tapada.
        Keep,
    }

    // Camadas das janelas das páginas, de cima para baixo.
    public enum Layer
    {
        // À vista e já apresentada (incluindo uma quente que voltou: o conteúdo dela já está no ecrã).
        Shown,
        // A sair, por cima da que entra até esta apresentar.
        Held,
        // A entrar fria: por baixo da que sai, a desenhar-se.
        Arriving,
        // Quente: tapada por todas as outras.
        Warm,
        // Saiu e vai ser escondida agora (não fica quente): por baixo de tudo, para que esconder-se não se veja.
        Cooling,
    }

    // Uma página pesada que volta de escondida leva ~200–300 ms a apresentar (Wikipédia, medido); uma aba nova espera
    // pelo fim do carregamento do 1.º documento (~100–250 ms na página nova aba). Nunca mais do que isto: uma página
    // que não responde não prende a antiga no ecrã.
    public static readonly TimeSpan HoldLimit = TimeSpan.FromMilliseconds(1000);

    // O requestAnimationFrame duplo resolve no início do 2.º fotograma; o Chromium e o DWM ainda levam até dois
    // fotogramas a pô-lo no ecrã (sem esta margem via-se, às vezes, 1 fotograma antigo da página que entra).
    public static readonly TimeSpan PresentMargin = TimeSpan.FromMilliseconds(34);

    // Corpo de uma função async (PageScript.EvaluateAsync com asyncBody): resolve quando a página já desenhou.
    public const string NextFrameScript = "await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); return true;";

    // Uma página no 1.º documento (aba nova, link aberto à frente): o rAF duplo NÃO chega. Medido a 60 fps na 1.ª aba nova
    // da sessão: o rAF duplo resolvia às vezes ~20 ms depois do fim do carregamento, com o 1.º fotograma ainda ~150 ms
    // longe do ecrã, e a página antiga saía antes do tempo — 5–6 fotogramas brancos (2 em 9 sessões). O que diz que um
    // fotograma com conteúdo JÁ ESTÁ no ecrã é a «first-contentful-paint»: o Chromium só a regista quando recebe a hora
    // da apresentação (medido: chega à página ~16 ms depois dela). Com ela, a página que entra já está no compositor do
    // Windows (por baixo da que sai) e não é precisa a margem PresentMargin. Sem ela no tempo FirstPaintWait (página sem
    // conteúdo, motor que não a dá), o rAF duplo e a margem, como antes.
    public static readonly TimeSpan FirstPaintWait = TimeSpan.FromMilliseconds(400);

    // Resposta do FirstPaintScript quando viu a «first-contentful-paint» (senão devolve true, como o NextFrameScript).
    public const string OnScreen = "no-ecra";

    public static readonly string FirstPaintScript = $$"""
        const painted = () => performance.getEntriesByName('first-contentful-paint').length > 0;
        const seen = painted() || await new Promise(r => {
          let o;
          const done = v => { if (o) o.disconnect(); r(v); };
          try { o = new PerformanceObserver(() => { if (painted()) done(true); }); o.observe({ type: 'paint', buffered: true }); }
          catch (e) { return r(false); }
          setTimeout(() => done(painted()), {{(int)FirstPaintWait.TotalMilliseconds}});
        });
        if (seen) return '{{OnScreen}}';
        {{NextFrameScript}}
        """;

    // O que a espera pela página que entra concluiu.
    public enum Presented
    {
        // Não apresentou dentro do tempo (ou fechou).
        No,
        // O rAF duplo resolveu: o fotograma está a caminho do ecrã (falta a margem PresentMargin).
        Frame,
        // A «first-contentful-paint» já foi apresentada: está no ecrã.
        OnScreen,
    }

    public static Presented FromScript(bool success, object? result)
        => !success ? Presented.No : result is string answer && answer == OnScreen ? Presented.OnScreen : result is true ? Presented.Frame : Presented.No;

    // A margem para o DWM só faz falta quando alguma página que entrou não está comprovadamente no ecrã.
    public static bool NeedsMargin(IEnumerable<Presented> arrivals) => arrivals.Any(p => p != Presented.OnScreen);

    // Para cada página: o que fazer agora. «Arrives» = passa a ver-se e é uma página do Chromium (só essas apresentam
    // um fotograma vazio ao aparecer). Sem nenhuma a chegar, as que saem escondem-se logo (como antes).
    public static IReadOnlyList<Step> Plan(IReadOnlyList<(bool WasVisible, bool Visible, bool Arrives)> pages)
        => Plan(pages.Select(p => (p.WasVisible, Warm: false, p.Visible, p.Arrives)).ToList());

    // «WasVisible» = a janela está visível (à vista ou quente); «Warm» = quente, tapada. Uma quente que volta não é uma
    // chegada (já está desenhada): sobe logo, sem segurar a que sai. Uma quente que continua fora de vista fica.
    public static IReadOnlyList<Step> Plan(IReadOnlyList<(bool WasVisible, bool Warm, bool Visible, bool Arrives)> pages)
    {
        var arriving = pages.Any(p => p.Visible && !p.WasVisible && p.Arrives);
        return pages.Select(p => p.Visible ? Step.Show : p.Warm ? Step.Keep : arriving && p.WasVisible ? Step.Hold : Step.Hide).ToList();
    }

    // Ordem das janelas das páginas, de cima para baixo: por camada e, dentro dela, por Rank crescente (quem chama dá
    // o Rank: a ordem por que começaram a sair, para as que saem; a mais recente primeiro, para as quentes).
    public static IReadOnlyList<T> StackOrder<T>(IEnumerable<(T Page, Layer Layer, long Rank)> pages)
        => pages.OrderBy(p => p.Layer).ThenBy(p => p.Rank).Select(p => p.Page).ToList();
}

// Páginas que chegaram frias e ainda não apresentaram um fotograma desde essa chegada. Uma página assim tem na janela
// uma superfície em branco ou antiga: não pode ficar «quente» (Tabs/WarmTabs.cs), porque voltar a uma quente é só subir
// a janela, sem esperar por nenhum fotograma. Cada chegada tem um número: a confirmação de uma chegada anterior (a
// página saiu e voltou entretanto) não conta para a atual.
public sealed class PagePresentation<T> where T : class
{
    private readonly Dictionary<T, long> _arrivals = new(ReferenceEqualityComparer.Instance);
    private long _serial;

    // A página passa a ver-se fria: fica por apresentar até Presented com este número.
    public long Arrive(T page) => _arrivals[page] = ++_serial;

    // O número da chegada por apresentar, ou null se a página já apresentou (ou nunca chegou fria).
    public long? Pending(T page) => _arrivals.TryGetValue(page, out var arrival) ? arrival : null;

    // A página apresentou depois da chegada «arrival»; só conta se for a chegada atual.
    public bool Presented(T page, long arrival)
        => _arrivals.TryGetValue(page, out var current) && current == arrival && _arrivals.Remove(page);

    public bool HasPresented(T page) => !_arrivals.ContainsKey(page);

    public void Forget(T page) => _arrivals.Remove(page);
}
