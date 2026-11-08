namespace Eteris.Sync.Transport;

// Transporte por uma pasta que a nuvem do próprio utilizador sincroniza (OneDrive, Dropbox, Google Drive para
// computador, pasta de rede, pen). Nenhum pedido de rede do Éteris: quem leva os ficheiros é o cliente da nuvem.
// Escrita atómica (temporário + mudança de nome na mesma pasta), por isso o cliente da nuvem nunca apanha um ficheiro
// a meio. Cada aparelho só escreve ficheiros seus, novos e imutáveis (exceto o seu estado), e não há «cópias em
// conflito»; as que aparecerem são ignoradas (SyncPaths).
public sealed class FolderTransport : ISyncTransport
{
    private const string TempMark = ".eteris-tmp-";
    private readonly string _root;

    public FolderTransport(string root, string? name = null)
    {
        _root = Path.GetFullPath(root);
        Name = name ?? "Pasta sincronizada";
    }

    public string Name { get; }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var start = Physical(prefix.EndsWith('/') ? prefix.TrimEnd('/') : DirectoryOf(prefix));
        if (!Directory.Exists(start)) return Task.FromResult<IReadOnlyList<string>>([]);
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Contains(TempMark, StringComparison.Ordinal)) continue;
            var logical = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (logical.StartsWith(prefix, StringComparison.Ordinal)) list.Add(logical);
        }
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = Physical(path);
        try { return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        var file = Physical(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileName(file) + TempMark + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = Physical(path);
        if (File.Exists(file)) File.Delete(file);
        // Pastas vazias de aparelhos/emparelhamentos saem também (até à raiz «eteris»).
        var dir = Path.GetDirectoryName(file);
        while (dir is not null && dir.Length > _root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return Task.CompletedTask;
    }

    private static string DirectoryOf(string prefix)
    {
        var slash = prefix.LastIndexOf('/');
        return slash < 0 ? string.Empty : prefix[..slash];
    }

    // Nome lógico → caminho dentro da raiz; nada de «..», raízes ou segmentos estranhos.
    private string Physical(string logical)
    {
        var segments = logical.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
            if (!SyncPaths.IsValidSegment(segment)) throw new ArgumentException("Nome de ficheiro inválido: " + logical, nameof(logical));
        var full = Path.GetFullPath(Path.Combine([_root, .. segments]));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fora da pasta: " + logical, nameof(logical));
        return full;
    }
}

// Transporte em memória: testes e a cópia direta entre dois objetos no mesmo processo.
public sealed class MemoryTransport : ISyncTransport
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Name => "Memória";

    public IReadOnlyDictionary<string, byte[]> Files
    {
        get { lock (_gate) return new Dictionary<string, byte[]>(_files, StringComparer.Ordinal); }
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
    }

    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_files.TryGetValue(path, out var data) ? (byte[]?)data.ToArray() : null);
    }

    public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        lock (_gate) _files[path] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) _files.Remove(path);
        return Task.CompletedTask;
    }

    // Para os testes de adulteração.
    public void Replace(string path, byte[] data)
    {
        lock (_gate) _files[path] = data;
    }
}
