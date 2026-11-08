using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Browser;

public sealed record Highlight(string Icon, string Title, string Detail, string? Keys = null);

// eteris://whatsnew — destaques da versão, mostrados uma vez depois de cada atualização. Os da versão nova vêm
// primeiro (6–8, curtos; o resto fica no CHANGELOG.md), seguidos dos das versões anteriores.
public static class WhatsNewPage
{
    public const string Url = "eteris://whatsnew/";

    public static readonly IReadOnlyList<Highlight> Highlights =
    [
        // 0.2.9
        new("▶", "Vídeo no motor do Éteris", "H.264, HEVC e AAC tocam na própria aba — Twitch, MP4, HLS, notícias — descodificados pela placa gráfica e pelo Windows, com o Escudo de sempre."),
        new("⚡", "Mais rápido, sem piscar", "Cada página abre quase 1 s mais cedo, Voltar e Avançar são instantâneos e abrir ou trocar de aba já não pisca."),
        new("◉", "Observatório do site", "No Escudo, «Abrir o Observatório» mostra ao vivo quem observa a página, por empresa, e bloqueia o que quiser. No cadeado, «O que este site sabe de ti».", "Ctrl+Shift+U"),
        new("📊", "Relatório e auto-teste de privacidade", "eteris://privacy/ conta o que o Escudo travou em 7 e 30 dias, mostra o que um site vê do Éteris e cada promessa com ✔ ou ✘."),
        new("↪", "Rastreio escondido", "Links por l.facebook.com ou out.reddit.com vão direto ao destino e os rastreadores atrás de um subdomínio do site (CNAME) são desmascarados."),
        new("🏠", "A rede local é sua", "Um site já não fala com este computador, o router ou a impressora sem perguntar; e os sites http:// abrem por HTTPS, com aviso quando não o têm."),
        new("▣", "Espaços", "Ambientes de abas na mesma janela — Pessoal, Trabalho, Estudo — cada um com nome, cor e emoji.", "Ctrl+Alt+1 … 9"),
        new("▥", "Barra lateral", "Notas, Para ler, Favoritos, Histórico e os sites que fixar, numa coluna ao lado da página.", "Ctrl+Shift+B"),
        // 0.2.8 e anteriores
        new("◈", "Costa Atlântica ao vivo", "Ativa Ao vivo na personalização da nova aba para ver as ondas e as nuvens em movimento, sem som."),
        new("◈", "Nova aba com paisagens", "Escolhe uma paisagem gerada por IA como fundo nas opções da nova aba. Os atalhos ficam junto da pesquisa, com ferramentas no rodapé."),
        new("▤", "Menu do Éteris", "Encontra as ações por categoria e controla o zoom no próprio menu.", "Alt+F"),
        new("◷", "Ícones no histórico", "O histórico e os atalhos mostram os ícones dos sites conforme voltas a visitá-los."),
        new("⚡", "Mais leve, como o Chrome", "As páginas gastam até 4× menos processador e as abas em segundo plano deixam de gastar — mais bateria e rolagem mais suave."),
        new("🚫", "Fim dos anúncios em nova aba", "Um clique na página já não abre anúncios noutra aba: só abrem os links em que clicou."),
        new("📖", "Modo de leitura do Firefox", "O Readability da Mozilla extrai os artigos com muito mais fiabilidade.", "F9"),
        new("🛡", "Privacidade a sério", "DNS cifrado, links sem rastreio, modo rigoroso por site, notificações bloqueadas e o histórico interno do Chromium desligado."),
        new("🗂", "Sessões guardadas", "Guarde as abas de uma janela com nome e reabra-as quando quiser — também a partir da nova aba.", "Ctrl+K"),
        new("♫", "Centro de multimédia", "Um botão mostra o que toca em todas as abas: tocar, pausar, silenciar, ir para a aba."),
        new("🔊", "Ouvir artigos", "No modo de leitura, «Ouvir» lê o artigo em voz alta com as vozes do Windows — nada sai do computador.", "F9"),
        new("⧉", "Janela flutuante", "A janela encolhe para um canto e fica por cima — para ver um vídeo enquanto trabalha."),
        new("🌙", "Escurecer este site", "Modo escuro só para os sites que escolher, lembrado da próxima vez."),
        new("📝", "Notas", "Um bloco de notas ao lado da página, com um clique para guardar o endereço."),
        new("▦", "Código QR da página", "Abra a página no telemóvel sem contas nem sincronização."),
        new("🎨", "Tema claro e Mica", "Escuro, claro ou o do Windows — e as barras deixam transparecer o ambiente de trabalho."),
        new("📚", "Grupos, abas verticais e ecrã dividido", "Grupos com nome e cor, uma lista lateral para dezenas de abas e duas páginas lado a lado."),
        new("⌨", "Paleta e atalhos", "Tudo à distância do teclado — abas de todas as janelas, comandos e «Colar e ir».", "Ctrl+K · F1"),
        new("🔗", "Links para um destaque", "Selecione texto e copie um link que abre a página já nesse parágrafo."),
        new("📸", "Capturas, PDF e favoritos", "A página inteira numa imagem ou em PDF; favoritos importados e exportados.", "Ctrl+Shift+S"),
        new("✂", "Esconder elementos", "Clique num anúncio ou caixa e desaparece para sempre nesse site."),
    ];

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(WhatsNewPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.whatsnew.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // Abre-se uma vez por versão (nunca no primeiro arranque, que tem as boas-vindas).
    public static bool ShouldShow(string lastSeenVersion, string currentVersion, bool firstRun)
        => !firstRun && !string.Equals(lastSeenVersion, currentVersion, StringComparison.Ordinal);

    public static string Render(string version)
    {
        var data = new { version, items = Highlights.Select(h => new { icon = h.Icon, title = h.Title, detail = h.Detail, keys = h.Keys }) };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
