using System.Globalization;
using System.Text;

namespace Eteris.Sync.Engine;

// O nome com que um computador aparece aos outros (lista de aparelhos, pedido de aprovação, avisos). É informação,
// nunca prova de quem é o aparelho. Um nome que chega de fora é escolhido por quem o mandou: limpa-se à saída.
public static class DeviceNameText
{
    public const int MaxLength = 60;

    // O texto que o produto já usava para um aparelho sem nome.
    public const string Unnamed = "Aparelho sem nome";

    // «Éteris em {nome do Windows}», já limpo. Se o nome do Windows vier vazio: «Éteris».
    public static string Default(string? windowsName)
        => Clean(windowsName) is { } name ? Clean("Éteris em " + name) ?? "Éteris" : "Éteris";

    // Tira caracteres de controlo e de formato (categorias Unicode Cc e Cf, que incluem os de inversão de sentido),
    // troca qualquer sequência de espaços por um só, apara as pontas e corta em 60 sem partir um par substituto.
    // null se não sobrar nada.
    public static string? Clean(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return null;
        var text = new StringBuilder(typed.Length);
        var space = false;
        // Por caracteres inteiros (Rune), não por unidades UTF-16: um carácter de formato fora do plano básico (as
        // etiquetas U+E0000–E007F, invisíveis) também sai, e um substituto solto (texto mal formado) é deitado fora.
        var rest = typed.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var used);
            rest = rest[used..];
            if (status != System.Buffers.OperationStatus.Done) continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = text.Length > 0;
                continue;
            }
            if (space) text.Append(' ');
            space = false;
            text.Append(rune.ToString());
        }
        if (text.Length > MaxLength)
        {
            var cut = MaxLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text.Length = cut;
            while (text.Length > 0 && text[^1] == ' ') text.Length--;
        }
        return text.Length == 0 ? null : text.ToString();
    }

    // Um nome que CHEGOU de outro computador, pronto a mostrar. Nunca null nem vazio.
    public static string Shown(string? received) => Clean(received) ?? Unnamed;
}
