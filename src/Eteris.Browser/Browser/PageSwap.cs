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

    // Para cada página: o que fazer agora. «Arrives» = passa a ver-se e é uma página do Chromium (só essas apresentam
    // um fotograma vazio ao aparecer). Sem nenhuma a chegar, as que saem escondem-se logo (como antes).
    public static IReadOnlyList<Step> Plan(IReadOnlyList<(bool WasVisible, bool Visible, bool Arrives)> pages)
    {
        var arriving = pages.Any(p => p.Visible && !p.WasVisible && p.Arrives);
        return pages.Select(p => p.Visible ? Step.Show : arriving && p.WasVisible ? Step.Hold : Step.Hide).ToList();
    }
}
