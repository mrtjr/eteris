namespace Eteris.Browser.Palette;

public sealed record Shortcut(string Keys, string Action);

public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Items);

// Todos os atalhos do Éteris, agrupados para a folha de atalhos (Ctrl+/ ou F1).
public static class ShortcutSheet
{
    public static readonly IReadOnlyList<ShortcutGroup> Groups =
    [
        new("Abas", [
            new("Ctrl+T", "Nova aba"),
            new("Ctrl+Shift+A", "Radar de abas"),
            new("Ctrl+W", "Fechar aba"),
            new("Ctrl+Shift+T", "Reabrir aba fechada"),
            new("Ctrl+Z", "Anular a última ação do Éteris (fechar, Feito, Adiar…) — fora da página e dos campos de texto"),
            new("Ctrl+Tab", "Aba seguinte"),
            new("Ctrl+Shift+Tab", "Aba anterior"),
            new("Ctrl+1 … 9", "Ir para a aba"),
            new("Alt+Home", "Voltar a casa (aba fixada)"),
            new("Clique do meio", "Fechar aba"),
            new("Ctrl+clique · Shift+clique", "Selecionar várias abas (ações em bloco no menu da aba)"),
            new("Arrastar aba à margem", "Ecrã dividido (direita: lado a lado · fundo: empilhado)"),
            new("Ctrl+Alt+1 … 9", "Ir para o Espaço"),
            new("Ctrl+Alt+← · Ctrl+Alt+→", "Espaço anterior · seguinte"),
        ]),
        new("Navegação", [
            new("Ctrl+L · F6", "Barra de endereço"),
            new("Ctrl+Shift+V", "Colar e ir (na barra de endereço)"),
            new("Alt+← · botão do rato", "Voltar"),
            new("Alt+→ · botão do rato", "Avançar"),
            new("F5 · Ctrl+R", "Recarregar"),
            new("Ctrl+Shift+R", "Recarregar sem cache"),
            new("Esc", "Parar o carregamento"),
            new("Alt+clique num link", "Espreitar o link num cartão, sem abrir aba"),
        ]),
        new("Ferramentas", [
            new("Alt+F · Alt+E", "Menu do Éteris"),
            new("Ctrl+K", "Paleta de comandos"),
            new("Ctrl+F · F3", "Procurar na página"),
            new("Ctrl+Shift+L", "Seguir link pelo teclado"),
            new("Ctrl+D", "Favorito"),
            new("Ctrl+H", "Histórico"),
            new("Ctrl+Shift+O", "Favoritos"),
            new("Ctrl+J", "Transferências"),
            new("Ctrl+Shift+S", "Capturar página inteira"),
            new("Ctrl+Shift+U", "Observatório do site (quem está a observar)"),
            new("Ctrl+,", "Definições"),
        ]),
        new("Vista", [
            new("F9", "Modo de leitura"),
            new("F11", "Modo foco (o rato no topo mostra as barras)"),
            new("Alt", "Mostrar as barras no modo foco"),
            new("Ctrl+Shift+M", "Janela flutuante"),
            new("Ctrl+.", "Pausa (silencia tudo e esconde as páginas; Esc volta)"),
            new("Ctrl+Shift+B", "Barra lateral (Notas, Para ler, Favoritos, Histórico)"),
            new("Ctrl+Shift+E", "Notas (na barra lateral)"),
            new("Ctrl + / Ctrl −", "Zoom (memorizado por site)"),
            new("Ctrl+0", "Repor zoom"),
            new("Ctrl + roda do rato", "Zoom"),
        ]),
        new("Janelas e página", [
            new("Ctrl+N", "Nova janela"),
            new("Ctrl+Shift+N", "Nova janela privada"),
            new("Ctrl+Alt+L", Tabs.PrivateLockText.ShortcutAction),
            new("Ctrl+P", "Imprimir"),
            new("Ctrl+U", "Ver código-fonte"),
            new("F12", "Ferramentas de programador"),
            new("Ctrl+/ · F1", "Esta folha de atalhos"),
        ]),
        new("Barra de endereço", [
            new("!w · !yt · !gh …", "Pesquisar noutro site"),
            new("2+2*3", "Contas instantâneas"),
            new("10 km em milhas", "Conversões"),
            new("Alt+Enter", "Abrir numa nova aba"),
        ]),
    ];
}
