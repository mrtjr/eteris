using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Browser;

public sealed record Highlight(string Icon, string Title, string Detail, string? Keys = null);

// eteris://whatsnew — destaques da versão, mostrados uma vez depois de cada atualização.
public static class WhatsNewPage
{
    public const string Url = "eteris://whatsnew/";

    public static readonly IReadOnlyList<Highlight> Highlights =
    [
        new("◉", "Observatório do site", "Clique no Escudo e em «Abrir o Observatório» para ver, ao vivo, quem está a observar a página: o site ao centro e cada domínio de terceiros à volta, vermelho bloqueado, verde permitido, âmbar exceção sua, agrupados por empresa. «Porque é que isto passou?» explica cada domínio permitido e «Bloquear neste site» resolve. No cadeado, «O que este site sabe de ti»: cookies, armazenamento, terceiros, referrer e rede local.", "Ctrl+Shift+U"),
        new("📊", "Relatório e auto-teste de privacidade", "eteris://privacy/ conta o que o Escudo travou nos últimos 7 e 30 dias — por dia, por categoria e por empresa, só números guardados neste computador e apagáveis — e o auto-teste mostra o que um site vê do Éteris (GPC, cookies de terceiros, referrer, língua, WebRTC, rede local, canvas) com ✔ ou ✘."),
        new("↪", "Rastreio escondido", "Links que passam por l.facebook.com, out.reddit.com ou bing.com/ck vão direto ao destino (os avisos de segurança, como o Safe Links do Outlook, ficam); os sites que só servem de salto entre dois outros perdem cookies e armazenamento uma hora depois; e os rastreadores escondidos atrás de um subdomínio do próprio site (CNAME) são desmascarados e bloqueados. O Escudo diz o que apanhou em cada página."),
        new("✔", "Promessas verificáveis", "eteris://privacy/ mostra o que o Éteris garante, cada promessa com ✔ ou ✘ lido do estado real: GPC visível aos sites, APIs de anúncios do Chromium desligadas, cada site com o seu frasco, referrer só com a origem, uma só língua, cookies de terceiros fora também dos pedidos à rede. E, se quiser, o Éteris esconde-se do Recall e das capturas (Definições › Privacidade ou o selo «Privada»)."),
        new("🏠", "A rede local é sua", "Um site da Internet já não fala com este computador nem com o router, a impressora ou o NAS sem perguntar: o Escudo mostra o que foi travado e um cartão deixa permitir só desta vez ou sempre."),
        new("🔒", "HTTPS primeiro, com aviso", "Os sites abertos por http:// vão por ligação cifrada; quando o site não a tem, o Éteris diz-lho em vez de recuar às escondidas — «Continuar sem segurança» só desta vez ou sempre para esse site. As páginas de erro dizem o que aconteceu (DNS, ligação recusada, certificado…) e copiam os detalhes."),
        new("↶", "Anular", "Feito, Adiar, Arquivo, abas fechadas em bloco e favoritos removidos têm «Anular» no aviso; Ctrl+Z fora da página anula a última ação do Éteris. PDF e capturas guardados abrem-se ou mostram-se na pasta a partir do aviso.", "Ctrl+Z"),
        new("🔥", "Esquecer e triturar", "No cadeado: «Esquecer este site ao fechar» apaga cookies, armazenamento, histórico e permissões quando fecha a última aba do site; «Triturar agora…» mostra o que vai apagar e apaga já. «Terminar sessão privada» no menu fecha todas as janelas privadas de uma vez."),
        new("▥", "Barra lateral", "Uma coluna ao lado da página, à esquerda ou à direita e com a largura que quiser: Notas, Para ler, Favoritos, Histórico e os sites que fixar — um chat ou um feed abre dentro da barra, com os cookies e o Escudo de sempre, e fica lá entre sessões.", "Ctrl+Shift+B"),
        new("▣", "Espaços", "Ambientes de abas na mesma janela (Pessoal, Trabalho, Estudo…), cada um com nome, cor e emoji: «Novo Espaço» na paleta cria um, os chips na barra de título mudam entre eles, «Mover para o Espaço…» no menu da aba (ou arrastar a aba até ao chip) leva abas de um para outro. As abas dos outros Espaços dormem e tudo fica na sessão.", "Ctrl+Alt+1 … 9"),
        new("✓", "Abas como tarefas", "No menu da aba: «Feito» fecha e guarda em «Feitas hoje», «Adiar até…» faz a aba voltar à hora escolhida, «Trancar» impede fechar por engano. Abas sem visita há dias desvanecem e vão para o Arquivo do Radar, de onde se repõem (Definições › Abas)."),
        new("✎", "Abas com nome, emoji e nota", "«Mudar o nome…» no menu da aba dá-lhe um nome próprio, um emoji e uma cor que ficam na sessão; «Escrever nota…» prende uma frase («porque abri isto?») que aparece no cartão da aba, no Radar e nas sessões guardadas. Pairar na etiqueta de um grupo mostra as abas dele."),
        new("👁", "Espreitar um link", "Alt+clique num link (ou «Espreitar o link» no menu) mostra o destino num cartão: título, imagem, tempo de leitura e os primeiros parágrafos — sem abrir aba nem deixar rasto no histórico.", "Alt+clique"),
        new("◫", "Ecrã dividido 2.0", "«Abrir link ao lado» em qualquer link, arrastar uma aba até à margem direita (lado a lado) ou inferior (empilhado), trocar lados pelo selo na barra de endereço — e a divisão fica na sessão."),
        new("⇆", "Abas que nunca se perdem", "Com muitas abas a faixa rola em vez de as esmagar: roda do rato, setas nas pontas e a lista «▾» de todas as abas, pesquisável."),
        new("▭", "Modo compacto", "Abas e barra de endereço numa só linha — a página ganha uma barra inteira. Em Definições › Aspeto › Densidade."),
        new("◉", "Cor do Éteris", "Escolhe a cor de destaque de toda a interface em Definições › Aspeto: nove cores, a da paisagem da nova aba ou a do Windows."),
        new("◫", "Interface que desaparece", "No modo foco as barras aparecem quando o rato toca no topo e escondem-se quando o deixa.", "F11"),
        new("◈", "Costa Atlântica ao vivo", "Ativa Ao vivo na personalização da nova aba para ver as ondas e as nuvens em movimento, sem som."),
        new("◈", "Nova aba com paisagens", "Escolhe uma paisagem gerada por IA como fundo nas opções da nova aba. Os atalhos ficam junto da pesquisa, com ferramentas no rodapé."),
        new("▤", "Menu do Éteris", "Encontra as ações por categoria e controla o zoom no próprio menu.", "Alt+F"),
        new("◷", "Ícones no histórico", "O histórico e os atalhos mostram os ícones dos sites conforme voltas a visitá-los."),
        new("⚡", "Mais leve, como o Chrome", "As páginas gastam até 4× menos processador e as abas em segundo plano deixam de gastar — mais bateria e rolagem mais suave."),
        new("▶", "Vídeo em todos os sites", "Sites que só têm vídeo em H.264, como o Twitch, passam sozinhos para o motor de vídeo do Windows — dentro do Éteris, com o mesmo bloqueador."),
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
