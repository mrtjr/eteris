using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Browser;

public sealed record Highlight(string Icon, string Title, string Detail, string? Keys = null);

// eteris://whatsnew — destaques da versão, mostrados uma vez depois de cada atualização. Os da versão nova vêm
// primeiro (4–8, curtos; o resto fica no CHANGELOG.md), seguidos dos das versões anteriores.
public static class WhatsNewPage
{
    public const string Url = "eteris://whatsnew/";

    // Destaque do isolamento das páginas (docs/sandbox.md). Só aparece enquanto o build/publish.ps1 publicar com o
    // sandbox por omissão: uma versão de recuo sem ele tira-o (WhatsNewTests.Highlights_AnnounceTheSandboxOnlyWhen…).
    public const string SandboxTitle = "Páginas isoladas (sandbox)";

    // O que as Novidades dizem numa publicação sem sandbox (a de emergência pelo CI, ou um PC sem ele): em vez do anúncio
    // do isolamento, o aviso de que esta versão corre sem ele (CAD-05, PRO-09).
    public const string NoSandboxTitle = "Esta versão corre sem isolamento das páginas";

    // Destaques do desempenho da 0.2.14, com os números da medição contra a 0.2.13 (docs/paridade.md, «Medição da
    // 0.2.14»). Os dois últimos só aparecem enquanto o ajuste respetivo estiver ligado por omissão no PerfTuning (e8, a
    // janela que não se vê; e13, o sono com pouca memória): desligar um deles por omissão obriga a tirar o destaque
    // (WhatsNewTests.Highlights_AnnounceTheTweaksOnlyWhileTheyAreOnByDefault).
    public const string LighterTitle = "Mais leve com muitas abas";
    public const string UnseenTitle = "Janela minimizada não gasta";
    public const string PressureTitle = "Abas que adormecem quando falta memória";

    public static readonly IReadOnlyList<Highlight> Highlights =
    [
        // 0.2.16 (textos conferidos com o CHANGELOG da 0.2.16: a sessão com todas as janelas normais, o sono que poupa as
        // abas com trabalho em curso, também pelo comando da paleta, e «Apagar dados de navegação» que apaga o armazenamento
        // dos sites e diz o que falhou; as atualizações conferidas com uma lista assinada valem a partir desta versão — a
        // 0.2.15 que descarrega a 0.2.16 ainda não confere)
        new("⧉", "Todas as janelas voltam", "Com mais de uma janela aberta, atualizar, encerrar o Windows ou uma queda já não deixam só as abas de uma: todas as janelas normais voltam, com os seus Espaços."),
        new("🎙", "Abas que não perdem trabalho", "Uma aba com uma chamada ou gravação, Picture-in-Picture ou texto por enviar já não adormece, nem pelo comando «Adormecer abas em segundo plano» da paleta."),
        new("🧹", "Limpar dados apaga mesmo", "«Cookies e dados de sites» apaga também o que os sites guardaram (armazenamento local, IndexedDB, service workers), e «Apagar dados» diz o que não saiu."),
        new("🔏", "Atualizações conferidas", "A partir desta versão, o Éteris só instala atualizações de uma lista assinada pelo autor, com uma chave fora do GitHub, e confere cada pacote descarregado."),
        // 0.2.15 («Um login e pronto»; textos conferidos com o código: pontos de entrada da T13, aprovação escrevendo o
        // código da T7/T8, kit de RecoveryCode.WordCount palavras da T5/T6, saídas da T12)
        new("☁", "Um login e pronto", "Entre com o Google pelo menu ⋯, pelas boas-vindas ou pelas Definições: escolhe a conta no seu navegador e a sincronização cifrada começa sozinha."),
        new("🔢", "Computador novo, aprovado com um número", "Ao entrar noutro computador, o pedido aparece nos que já estão ligados: escreva lá o código que o novo mostra e ele entra, sem copiar chaves."),
        new("🗝", "Kit de recuperação em 24 palavras", "Guarde-o como ficheiro ou imprima-o, e volte a vê-lo num computador ligado, com o Windows Hello. Os códigos antigos continuam a servir."),
        new("🚪", "Sair só deste computador", "«Sair deste computador» já não desliga os outros. Para desligar todos há «Retirar o acesso…»; se perdeu um, use «Perdi um computador»."),
        // 0.2.14
        new("🔐", SandboxTitle, "Como no Chrome, no Firefox e no Brave: cada página corre fechada, sem acesso aos seus ficheiros nem ao sistema. Se o isolamento falhar neste PC, o Éteris abre sem ele e avisa."),
        new("📉", LighterTitle, "Medido contra a 0.2.13: 11% a 16% menos memória e, com 20 abas abertas e paradas, o processador desce de 1,72% para 0,13% de um núcleo."),
        new("⏸", UnseenTitle, "Com a janela minimizada, uma página animada deixa de desenhar, como no Chrome, no Firefox e no Brave: 6,47% → 0,59% de um núcleo. Nunca com som, multimédia ou câmara em uso."),
        new("💤", PressureTitle, "Com a memória do Windows quase esgotada, adormece a aba usada há mais tempo e, ao acordar, a página volta ao mesmo ponto. Desliga-se em Definições › Abas."),
        // 0.2.13
        new("☁", "Conta Google e sincronização cifrada", "Opcional: favoritos, Espaços, definições, Escudo, notas e palavras-passe passam entre computadores, cifrados no PC antes de irem para a pasta oculta do seu Google Drive."),
        new("🔑", "Palavras-passe e preenchimento", "Guardar, preencher e gerar palavras-passe, importar do Chrome, do Brave ou do Firefox; moradas, cartões (nunca o CVV), documentos e viagens, com o Windows Hello."),
        new("✂", "Formulários sem pedidos à Google", "Ao entrar num site, o motor mandava à Google um prefixo do hash do utilizador e da palavra-passe (verificação de fugas do Chromium). Cortado: 0 pedidos, medido."),
        // 0.2.12
        new("⌨", "Menu do Éteris pelo teclado", "Como no Chrome, no Firefox e no Brave: setas, Home, End e a inicial escolhem a linha, Enter ativa, Esc fecha e devolve o foco, e as teclas já não chegam à página.", "Alt+F · Alt+E"),
        new("☰", "Menu legível", "O título, o subtítulo e o «Zoom» do menu já não aparecem em quadrados, e nas Definições a secção escolhida na navegação fica realçada."),
        // 0.2.11
        new("🔒", "No arranque, nada sai para a Google", "Abrir o Éteris já não pede a hora à Google nem pergunta pelo «Modo IA»: 0 pedidos no arranque, medido com um proxy, e a promessa à vista em eteris://privacy/."),
        new("🛡", "Escudo também nos service workers", "Os pedidos sem aba — service workers, shared workers e extensões — passam pelo Escudo como os da página e contam no Escudo do site."),
        // 0.2.10
        new("⚡", "Arranque quase duas vezes mais rápido", "A primeira página aparece em ~0,6 s em vez de ~1,1 s: o motor, o desenho da janela e a base de dados arrancam ao mesmo tempo que a janela."),
        new("🪶", "Menos memória", "Cerca de 50 MB a menos: o Escudo guarda as listas de filtros já compiladas no perfil e não as volta a compilar em cada arranque."),
        new("🛡", "Impressão digital como a do Chrome", "Os sites já não veem os nomes da câmara, do microfone e dos altifalantes sem autorização, nem o «Não rastrear»; o GPC continua."),
        new("🌐", "Na língua do Windows", "Os sites recebem a língua do Windows, como no Chrome, e menos dicas do cliente; quem escolheu outra língua nas Definições mantém-na."),
        new("✔", "Correções", "Ícone certo nos vídeos abertos diretamente, o mesmo título no histórico nos dois motores, Espreitar à primeira e ecrã dividido sem saltos."),
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

    // Os destaques para este arranque: sem sandbox, o anúncio do isolamento das páginas dá lugar ao aviso de que esta versão
    // corre sem ele (os outros e a ordem não mudam).
    public static IReadOnlyList<Highlight> HighlightsFor(bool sandbox)
        => sandbox
            ? Highlights
            : [.. Highlights.Select(h => h.Title != SandboxTitle ? h
                : new Highlight("⚠", NoSandboxTitle, "Publicação de emergência: nesta versão as páginas correm sem sandbox, como até à 0.2.13. A versão seguinte volta a trazê-lo."))];

    public static string Render(string version) => Render(version, sandbox: true);

    public static string Render(string version, bool sandbox)
    {
        var data = new { version, items = HighlightsFor(sandbox).Select(h => new { icon = h.Icon, title = h.Title, detail = h.Detail, keys = h.Keys }) };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
