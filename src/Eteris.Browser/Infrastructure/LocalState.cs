using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eteris.Browser.Infrastructure;

// «Local State» do Chromium (preferências globais, lidas no arranque). O DNS cifrado vive aqui e não nas
// preferências do perfil, por isso é escrito antes de o CEF arrancar. Um ficheiro ilegível nunca é reescrito.
public static class LocalState
{
    public static bool ApplySecureDns(string path, string mode, string templates)
    {
        try
        {
            JsonNode root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject() : new JsonObject();
            if (root is not JsonObject obj) return false;
            obj["dns_over_https"] = new JsonObject { ["mode"] = mode, ["templates"] = templates };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, obj.ToJsonString());
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }
}
