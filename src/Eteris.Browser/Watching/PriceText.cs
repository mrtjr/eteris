using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Watching;

// Lê o preço que uma página mostra em texto («R$ 1.299,90», «1 299,90 €», «$1,299.90», «1234.56» dos dados
// estruturados) e escreve-o com os separadores portugueses. É só texto: a escolha de qual preço conta na página (os dados
// estruturados à frente das prestações) é feita por quem chama.
public static partial class PriceText
{
    // O primeiro número do texto: algarismos com grupos separados por «.» ou «,», ou por um espaço (normal, inseparável,
    // fino ou estreito) seguido de exatamente 3 algarismos. O grupo é atómico e o número não pode vir colado a uma letra
    // nem a outro algarismo, para «12x sem juros» (prestações) e «3GB» não passarem por preços.
    [GeneratedRegex(@"(?<![\d.,])(?>\d+(?:[.,]\d+|[    ]\d{3}(?!\d))*)(?![\p{L}\d])")]
    private static partial Regex NumberPattern();

    private static readonly NumberFormatInfo Portuguese = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = [3],
    };

    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = NumberPattern().Match(text);
        if (!match.Success) return null;

        // Os espaços dentro do número separam sempre milhares.
        var token = new StringBuilder(match.Value.Length);
        foreach (var c in match.Value)
            if (char.IsAsciiDigit(c) || c is '.' or ',') token.Append(c);
        var number = token.ToString();

        // O último «.» ou «,» seguido de 1–2 algarismos no fim é o separador decimal; seguido de 3 separa milhares. Com 4
        // ou mais algarismos depois («0.0001») também é decimal: não há grupo de milhares com mais de 3.
        var last = number.LastIndexOfAny(['.', ',']);
        string integer, fraction;
        if (last < 0)
        {
            integer = number;
            fraction = "";
        }
        else if (number.Length - last - 1 == 3)
        {
            integer = number;
            fraction = "";
        }
        else
        {
            integer = number[..last];
            fraction = number[(last + 1)..];
        }
        integer = integer.Replace(".", "").Replace(",", "");
        var invariant = fraction.Length == 0 ? integer : integer + "." + fraction;

        return decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // A moeda pelo símbolo do texto: «R$» dá BRL e «€» dá EUR; outro símbolo (ou nenhum) não diz a moeda.
    public static string? CurrencyOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("R$", StringComparison.Ordinal)) return "BRL";
        if (text.Contains('€')) return "EUR";
        return null;
    }

    // Agrupamento pt-PT com «.» e «,» e 2 casas decimais; «BRL» põe «R$ » à frente, «EUR» põe « €» no fim e qualquer
    // outra moeda (ou nenhuma) fica sem símbolo.
    public static string Format(decimal value, string? currency)
    {
        var number = value.ToString("#,##0.00", Portuguese);
        if (string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase)) return "R$ " + number;
        if (string.Equals(currency, "EUR", StringComparison.OrdinalIgnoreCase)) return number + " €";
        return number;
    }
}
