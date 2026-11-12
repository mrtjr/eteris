using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eteris.Browser.Infrastructure;

// «Local State» do Chromium (preferências globais, lidas no arranque). O DNS cifrado vive aqui e não nas
// preferências do perfil, por isso é escrito antes de o CEF arrancar. Um ficheiro ilegível nunca é reescrito.
public static class LocalState
{
    public static bool ApplySecureDns(string path, string mode, string templates) => Apply(path, mode, templates, energySaver: null);

    // O DNS cifrado e, na mesma leitura e na mesma escrita (nada a mais no disco antes da 1.ª página), o estado da
    // poupança de energia do Chromium (ajuste e16, Infrastructure/EnergySaver.cs): com um valor, escreve-o; sem ele (o
    // ajuste desligado), tira a chave se lá estiver, e vale o do Chromium (1, «com bateria baixa»), o de sempre. Assim o
    // --eteris-sem-ajustes também desfaz um estado escrito antes.
    public static bool Apply(string path, string mode, string templates, int? energySaver)
    {
        try
        {
            JsonNode root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject() : new JsonObject();
            if (root is not JsonObject obj) return false;
            var before = obj.ToJsonString();
            obj["dns_over_https"] = new JsonObject { ["mode"] = mode, ["templates"] = templates };
            if (energySaver is { } state) SetPath(obj, EnergySaver.PrefPath, state);
            else RemovePath(obj, EnergySaver.PrefPath);
            var after = obj.ToJsonString();
            // Sem alterações não se reescreve: o motor lê este ficheiro em cada arranque.
            if (File.Exists(path) && before == after) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path, after, backup: false);
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // As preferências com pontos no nome guardam-se em objetos encaixados («a.b.c» → {"a":{"b":{"c":…}}}).
    private static void SetPath(JsonObject obj, IReadOnlyList<string> path, int value)
    {
        var node = obj;
        foreach (var key in path.Take(path.Count - 1))
        {
            if (node[key] is not JsonObject child)
            {
                child = new JsonObject();
                node[key] = child;
            }
            node = child;
        }
        node[path[^1]] = value;
    }

    // Tira a chave e os objetos que ficarem vazios por causa dela.
    private static void RemovePath(JsonObject obj, IReadOnlyList<string> path)
    {
        var chain = new List<JsonObject> { obj };
        foreach (var key in path.Take(path.Count - 1))
        {
            if (chain[^1][key] is not JsonObject child) return;
            chain.Add(child);
        }
        if (!chain[^1].Remove(path[^1])) return;
        for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(path[i - 1]);
    }
}
