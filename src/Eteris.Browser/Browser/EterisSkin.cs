namespace Eteris.Browser.Browser;

// A pele comum das páginas internas (eteris://), posta por InternalPages.Themed logo a seguir ao <head>: antes dos
// estilos da própria página, que assim a podem afinar. É a mesma nos dois temas (muda só a classe html.light, que o Themed
// já põe) e na janela privada.
//   - Barras de deslocamento de 10 px, sem setas, arredondadas, com a calha transparente e o polegar a seguir o tema. O
//     polegar tem contraste de pelo menos 3:1 com o fundo das páginas nos dois temas (EterisSkinTests calcula-o): os
//     valores da proposta (.22 e .26) ficavam abaixo de 2:1 em repouso.
//   - Nenhuma página interna usa scrollbar-color nem scrollbar-width: no Chromium 121 e seguintes, qualquer das duas
//     desliga o ::-webkit-scrollbar do elemento (EterisSkinTests garante-o em todas as páginas e no leitor).
//   - Um anel de foco visível em todos os controlos (:focus-visible), com especificidade zero (:where): uma página que
//     já desenha o seu foco (input { outline: none } e a borda íris do Histórico) fica com ele, sem dois anéis.
//   - «Reduzir movimento» do Windows (Efeitos de animação desligados): sem transições nem animações.
//   - Alto contraste do Windows (forced-colors): as barras voltam a ser as do sistema.
//   - Os raios --r-* ficam disponíveis; a migração dos raios das páginas é do ciclo do «sistema visual».
// Só ASCII: vem antes do <meta charset> da página.
public static class EterisSkin
{
    public const string Id = "eteris-skin";

    public const string Css = """
        :root { --r-s: 6px; --r-m: 10px; --r-l: 14px; --r-xl: 22px;
                --sb-thumb: rgba(233, 235, 242, .40); --sb-thumb-hover: rgba(233, 235, 242, .58); }
        html.light { --sb-thumb: rgba(27, 31, 42, .50); --sb-thumb-hover: rgba(27, 31, 42, .66); }
        ::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }
        ::-webkit-scrollbar-button { display: none; }
        ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
        ::-webkit-scrollbar-thumb { background: var(--sb-thumb); border-radius: 999px;
                                    border: 2px solid transparent; background-clip: padding-box; }
        ::-webkit-scrollbar-thumb:hover { background-color: var(--sb-thumb-hover); }
        :where(:focus-visible) { outline: 2px solid var(--accent, #22d3ee); outline-offset: 2px; }
        @media (prefers-reduced-motion: reduce) {
          *, *::before, *::after { animation-duration: .01ms !important; animation-iteration-count: 1 !important;
                                   transition-duration: .01ms !important; scroll-behavior: auto !important; }
        }
        @media (forced-colors: active) {
          ::-webkit-scrollbar, ::-webkit-scrollbar-thumb, ::-webkit-scrollbar-track { all: unset; }
        }
        """;

    public const string Style = "<style id=\"" + Id + "\">" + Css + "</style>";
}
