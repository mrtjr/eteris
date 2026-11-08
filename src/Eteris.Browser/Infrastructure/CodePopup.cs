using System.Windows;
using System.Windows.Documents;

namespace Eteris.Browser.Infrastructure;

// Popups e menus criados em código não têm pai lógico, e o WPF faz-lhes herdar as propriedades do PlacementTarget. Se o
// alvo é um botão de ícone (estilo IconButton: FontFamily = IconFont, a Segoe Fluent Icons, que não tem letras), todo o
// texto sem fonte própria lá dentro sai em quadrados. A raiz do conteúdo fixa a fonte da interface; os ícones continuam
// a pôr IconFont explicitamente.
public static class CodePopup
{
    public const string UiFontKey = "UiFont";

    public static T WithUiFont<T>(T root) where T : FrameworkElement
    {
        root.SetResourceReference(TextElement.FontFamilyProperty, UiFontKey);
        return root;
    }
}
