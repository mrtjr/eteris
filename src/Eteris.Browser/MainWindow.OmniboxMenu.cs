using System.Windows;
using System.Windows.Controls;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser;

// Menu do botão direito da barra de endereço: no tema do Éteris (em vez do menu claro do Windows) e com «Colar e ir».
public partial class MainWindow
{
    public static string PasteAndGoLabel(string clipboard, SearchEngine engine)
    {
        var text = string.Join(' ', clipboard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (OmniboxClassifier.Classify(text, engine) is not { } result) return "Colar e ir";
        if (!result.IsSearch) return Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) ? $"Colar e ir para {uri.Host}" : "Colar e ir";
        var quote = text;
        if (text.Length > 28)
        {
            var cut = text[..28];
            var space = cut.LastIndexOf(' ');
            quote = (space > 10 ? cut[..space] : cut[..27]).TrimEnd() + "…";
        }
        return $"Colar e pesquisar «{quote}»";
    }

    private void PasteAndGo()
    {
        string clip;
        try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (Exception) { return; }
        var text = string.Join(' ', clip.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return;
        SetOmniboxText(text);
        CloseSuggestions();
        Go(text);
    }

    private void WatchOmniboxMenu()
    {
        Omnibox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers ==
                (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                e.Handled = true;
                PasteAndGo();
            }
        };
        // Com ContextMenu nulo, a TextBox monta o menu claro do Windows num manipulador de classe (antes do nosso);
        // um menu vazio desliga isso, e o ContextMenuOpening tratado abre o menu do Éteris no lugar dele.
        Omnibox.ContextMenu = new ContextMenu();
        Omnibox.ContextMenuOpening += OpenOmniboxMenu;
    }

    private void OpenOmniboxMenu(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        string clip;
        try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (Exception) { clip = string.Empty; }
        var hasClip = clip.Trim().Length > 0;
        var hasSelection = Omnibox.SelectionLength > 0;

        new DarkMenu()
            .Item("", "Desfazer", () => Omnibox.Undo(), "Ctrl+Z", Omnibox.CanUndo)
            .Separator()
            .Item("", "Cortar", () => Omnibox.Cut(), "Ctrl+X", hasSelection)
            .Item("", "Copiar", () => Omnibox.Copy(), "Ctrl+C", hasSelection)
            .Item("", "Colar", () => Omnibox.Paste(), "Ctrl+V", hasClip)
            .Item("", hasClip ? PasteAndGoLabel(clip, Engine) : "Colar e ir", PasteAndGo, "Ctrl+Shift+V", hasClip)
            .Separator()
            .Item("", "Selecionar tudo", () => Omnibox.SelectAll(), "Ctrl+A", Omnibox.Text.Length > 0)
            .Open(Omnibox);
    }
}
