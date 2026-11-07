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
        catch (IOException) { return []; }
    }

    public void Save(IReadOnlyList<WebPanel> panels)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, WebPanels.Serialize(panels), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
