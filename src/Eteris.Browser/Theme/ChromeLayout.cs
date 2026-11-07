namespace Eteris.Browser.Theme;

// Disposição da moldura (barras à volta da página). Um só sítio decide as alturas e onde ficam as abas e os
// botões da janela, para as combinações (abas no topo, verticais, modo compacto, modo foco, ecrã inteiro) não se
// contradizerem. A janela só aplica o resultado (MainWindow.SideTabs.cs › ApplyChromeLayout).
public enum ChromeMode
{
    // Abas numa barra de título própria, barra de ferramentas por baixo (o clássico).
    Top,
    // Abas numa barra lateral; a barra de ferramentas é a barra de título.
    Side,
    // Abas e endereço numa só linha: a página ganha a altura da barra de título.
    Compact,
    // Modo foco ou ecrã inteiro: só a página.
    Hidden,
}

// Peeking: as barras do modo escondido estão à vista por o rato ter tocado no topo (ver AutoHideBars). Ocupam o
// seu lugar normal, mas a janela continua sem zona de arrastar nem cantos (está em modo foco ou ecrã inteiro).
public sealed record ChromeLayout(ChromeMode Mode, double TitleHeight, double ToolbarHeight, bool Peeking = false)
{
    public const double TitleBarHeight = 42;
    public const double ToolbarBarHeight = 48;
    public const double CompactBarHeight = 44;

    // Largura da barra de endereço em modo compacto: parada e a escrever (cresce ao receber o foco).
    public const double CompactOmniboxWidth = 360;
    public const double CompactOmniboxFocusedWidth = 560;

    public bool ShowsTitleBar => Mode == ChromeMode.Top;
    public bool ShowsToolbar => Mode != ChromeMode.Hidden;
    public bool ShowsSideBar => Mode == ChromeMode.Side;
    // Os botões da janela (minimizar, maximizar, fechar) vivem na barra de ferramentas quando não há barra de título.
    public bool CaptionInToolbar => Mode is ChromeMode.Side or ChromeMode.Compact;
    public bool TabsInToolbar => Mode == ChromeMode.Compact;
    // Zona de arrastar a janela: a barra de cima, seja ela qual for.
    public double CaptionHeight => Mode == ChromeMode.Hidden || Peeking ? 0 : Mode == ChromeMode.Top ? TitleHeight : ToolbarHeight;
    public double PageRadius => Mode == ChromeMode.Hidden || Peeking ? 0 : 10;

    // Modo foco e ecrã inteiro ganham a tudo (a não ser que o rato tenha chamado as barras: peek); as abas verticais
    // ganham ao compacto (a lateral já poupa a barra de título).
    public static ChromeLayout Resolve(bool verticalTabs, bool compact, bool hidden, bool peek = false)
    {
        if (hidden && peek) return Resolve(verticalTabs, compact, hidden: false) with { Peeking = true };
        if (hidden) return new(ChromeMode.Hidden, 0, 0);
        if (verticalTabs) return new(ChromeMode.Side, 0, ToolbarBarHeight);
        if (compact) return new(ChromeMode.Compact, 0, CompactBarHeight);
        return new(ChromeMode.Top, TitleBarHeight, ToolbarBarHeight);
    }

    // Largura da barra de endereço: fixa em modo compacto (cresce com o foco), toda a que houver nos outros.
    public static double? OmniboxWidth(ChromeMode mode, bool focused)
        => mode == ChromeMode.Compact ? focused ? CompactOmniboxFocusedWidth : CompactOmniboxWidth : null;
}
