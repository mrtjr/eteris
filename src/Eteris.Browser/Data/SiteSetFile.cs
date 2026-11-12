using System.IO;

namespace Eteris.Browser.Data;

// Um conjunto pequeno de sites num ficheiro de texto (um por linha), lido uma vez e regravado a cada mudança; sem
// nenhum, o ficheiro sai. Usado para os sites com «Esquecer ao fechar» que têm alguma coisa por apagar no perfil do
// motor de vídeo (AppData.VideoForgetPending). Uma falha do disco não sai daqui: o conjunto fica em memória.
public sealed class SiteSetFile(string path)
{
    private readonly Lock _gate = new();
    private HashSet<string>? _sites;

    public bool Contains(string site)
    {
        lock (_gate) return Sites().Contains(site);
    }

    public void Add(string site)
    {
        lock (_gate)
        {
            if (Sites().Add(site)) Save();
        }
    }

    public void Remove(string site)
    {
        lock (_gate)
        {
            if (Sites().Remove(site)) Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (Sites().Count == 0) return;
            _sites!.Clear();
            Save();
        }
    }

    private HashSet<string> Sites()
    {
        if (_sites is not null) return _sites;
        _sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                    if (line.Trim() is { Length: > 0 } site) _sites.Add(site);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _sites;
    }

    private void Save()
    {
        try
        {
            if (_sites!.Count == 0)
            {
                File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllLines(temp, _sites.Order(StringComparer.Ordinal));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
