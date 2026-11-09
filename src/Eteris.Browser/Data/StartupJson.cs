using System.Text.Json.Serialization;

namespace Eteris.Browser.Data;

// E12 do plano de desempenho da 0.2.14 (ajuste e12): o settings.json e o session.json lidos no arranque com o código
// gerado na compilação, sem a recolha de metadados por reflexão (e sem boa parte do JIT da 1.ª leitura). Opções por
// omissão, as mesmas da leitura por reflexão (nomes como estão, sensível a maiúsculas): os ficheiros leem-se iguais
// (StartupJsonTests compara as duas leituras). Só a leitura: a escrita fica como está (não é do arranque).
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(Session))]
internal sealed partial class StartupJsonContext : JsonSerializerContext;
