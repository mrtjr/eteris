namespace Eteris.Sync.Transport;

// Transporte por uma pasta que a nuvem do próprio utilizador sincroniza (OneDrive, Dropbox, Google Drive para
// computador, pasta de rede, pen). Nenhum pedido de rede do Éteris: quem leva os ficheiros é o cliente da nuvem.
// Escrita atómica (temporário + mudança de nome na mesma pasta), por isso o cliente da nuvem nunca apanha um ficheiro
// a meio. Cada aparelho só escreve ficheiros seus, novos e imutáveis (exceto o seu estado), e não há «cópias em
// conflito»; as que aparecerem são ignoradas ao ler (SyncPaths) e saem ao apagar (DeleteAsync).
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
        var file = PhysicalToDelete(path);
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

    // Apagar aceita também os nomes fora do esquema que a própria pasta lista dentro de «eteris»: as «cópias em
    // conflito» que o cliente da nuvem cria a partir dos ficheiros do Éteris («keys (1)», «000000000003 (Cópia em
    // conflito de Trabalho)»). Estão na pasta do Éteris e são cópias de ficheiros dele (cifrados): saem com «Apagar os
    // meus dados…» e nas limpezas, em vez de as travar. Ler e escrever continuam só com os nomes do esquema. Nunca fora
    // de «eteris», nem «.»/«..», separadores, «:» (fluxos NTFS), carateres proibidos ou nomes que o Windows encurtaria.
    private string PhysicalToDelete(string logical)
    {
        if (!logical.StartsWith(SyncPaths.Root, StringComparison.Ordinal)) return Physical(logical);
        var segments = logical.Split('/');
        foreach (var segment in segments)
            if (!SyncPaths.IsValidSegment(segment) && !IsPlainFileName(segment))
                throw new ArgumentException("Nome de ficheiro inválido: " + logical, nameof(logical));
        var full = Path.GetFullPath(Path.Combine([_root, .. segments]));
        var mine = Path.Combine(_root, SyncPaths.Root.TrimEnd('/')) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(mine, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fora da pasta: " + logical, nameof(logical));
        return full;
    }

    private static bool IsPlainFileName(string segment)
        => segment.Length is > 0 and <= 255 && segment is not "." and not ".." && !segment.EndsWith('.') && !segment.EndsWith(' ')
           && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !segment.Contains(':');

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
