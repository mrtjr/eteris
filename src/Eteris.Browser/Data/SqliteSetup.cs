using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Definições que cada ligação ao eteris.db tem de repetir.
internal static class SqliteSetup
{
    // O SQLite não apaga as páginas: «DELETE» só as marca como livres e o texto (endereços, títulos) continua legível
    // no ficheiro. Com secure_delete o conteúdo apagado é substituído por zeros. A definição é por ligação e as
    // ligações são reaproveitadas pelo Pooling: por isso cada Open() dos armazéns com endereços a repete.
    internal static void SecureDelete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON;";
        command.ExecuteNonQuery();
    }
}
