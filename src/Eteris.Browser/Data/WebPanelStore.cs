using System.IO;
using System.Text;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Data;

// Sites fixados na barra lateral: um ficheiro JSON no perfil (paineis.json), escrito de forma atómica.
public sealed class WebPanelStore(string path)
{
    public IReadOnlyList<WebPanel> Load()
    {
        try { return File.Exists(path) ? WebPanels.Parse(File.ReadAllText(path, Encoding.UTF8)) : []; }
        // Ficheiro ilegível (permissões negadas, preso por outro programa): sem sites fixados, nunca uma janela que cai.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // Alteração a partir do que está no ficheiro agora, não de uma cópia antiga: com duas janelas abertas, fixar
    // num lado não apaga o que o outro fixou. change devolve null quando não há nada a mudar (fica tudo como estava).
    public IReadOnlyList<WebPanel>? Update(Func<IReadOnlyList<WebPanel>, IReadOnlyList<WebPanel>?> change)
    {
        if (change(Load()) is not { } list) return null;
        Save(list);
        return list;
    }

    public void Save(IReadOnlyList<WebPanel> panels)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, WebPanels.Serialize(panels), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
