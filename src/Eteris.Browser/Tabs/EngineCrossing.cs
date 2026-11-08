namespace Eteris.Browser.Tabs;

// Voltar e Avançar entre os dois motores de uma aba (motor principal ↔ motor de vídeo, ver Tabs/VideoEngine.cs). Cada
// motor só conhece o seu histórico: sem isto, depois da troca o Voltar ficava desativado e perdia-se a página anterior.
// Como no Chrome, a entrada do motor de vídeo toma o lugar da página em que se trocou; o Voltar a partir da 1.ª entrada
// do motor de vídeo regressa ao motor principal na página anterior, e o Avançar daí volta ao vídeo.
public sealed class EngineCrossing
{
    public enum Step { None, InEngine, ToMain, ToVideo }

    // A página do motor principal a que o Voltar da 1.ª entrada do motor de vídeo regressa.
    public string? BackUrl { get; private set; }

    // Depois desse regresso: a página do motor de vídeo de onde se voltou (o Avançar do motor principal volta a ela).
    public string? ForwardUrl { get; private set; }

    // O endereço a que se regressou não volta sozinho para o motor de vídeo (site lembrado): foi lá que se quis ir.
    private string? _returning;

    // A aba entrou no motor de vídeo vinda do motor principal (back = a página anterior, ou null se não há).
    public void EnterVideo(string? back)
    {
        BackUrl = string.IsNullOrEmpty(back) ? null : back;
        ForwardUrl = null;
        _returning = null;
    }

    public bool CanGoBack(bool inVideo, bool engineCanGoBack) => engineCanGoBack || (inVideo && BackUrl is not null);

    public bool CanGoForward(bool inVideo, bool engineCanGoForward) => engineCanGoForward || (!inVideo && ForwardUrl is not null);

    public Step Back(bool inVideo, bool engineCanGoBack)
        => engineCanGoBack ? Step.InEngine : inVideo && BackUrl is not null ? Step.ToMain : Step.None;

    public Step Forward(bool inVideo, bool engineCanGoForward)
        => engineCanGoForward ? Step.InEngine : !inVideo && ForwardUrl is not null ? Step.ToVideo : Step.None;

    // Voltar da 1.ª entrada do motor de vídeo: o endereço do motor principal a abrir; o vídeo fica para o Avançar.
    public string? ReturnToMain(string videoUrl)
    {
        if (BackUrl is not { } url) return null;
        BackUrl = null;
        ForwardUrl = videoUrl;
        _returning = url;
        return url;
    }

    // Avançar do motor principal para a página de vídeo de onde se voltou (EnterVideo fixa depois o novo Voltar).
    public string? ForwardToVideo()
    {
        if (ForwardUrl is not { } url) return null;
        ForwardUrl = null;
        _returning = null;
        return url;
    }

    // O histórico do motor principal mudou: uma entrada nova depois do regresso (já há Voltar) corta o Avançar para o
    // vídeo, como num histórico normal.
    public void MainHistoryChanged(bool canGoBack)
    {
        if (canGoBack) ForwardUrl = null;
    }

    // O endereço a que se regressou fica no motor principal; qualquer outro endereço desfaz a exceção.
    public bool StaysInMain(string? address)
    {
        if (_returning is null) return false;
        if (string.Equals(address, _returning, StringComparison.Ordinal)) return true;
        _returning = null;
        return false;
    }

    // A página a que o Voltar do motor de vídeo deve regressar, a partir do histórico do motor principal no momento da
    // troca (endereços, do mais antigo para o mais recente, e o índice da entrada atual): a anterior à atual, se a atual
    // é a página que passa para o motor de vídeo; senão (o Avançar de volta ao vídeo) a própria atual.
    public static string? BackTarget(IReadOnlyList<string> entries, int currentIndex, string switchingTo)
    {
        if (currentIndex < 0 || currentIndex >= entries.Count) return null;
        if (!SamePage(entries[currentIndex], switchingTo)) return entries[currentIndex];
        return currentIndex > 0 ? entries[currentIndex - 1] : null;
    }

    // A mesma página, ignorando o fragmento (#t= da retoma do vídeo).
    private static bool SamePage(string a, string b)
        => string.Equals(WithoutFragment(a), WithoutFragment(b), StringComparison.OrdinalIgnoreCase);

    private static string WithoutFragment(string url) => url.IndexOf('#') is var hash and >= 0 ? url[..hash] : url;
}
