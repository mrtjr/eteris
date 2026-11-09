using System.Globalization;

namespace Eteris.Sync.Model;

// Relógio lógico híbrido (HLC): milissegundos do relógio + contador + aparelho. Ordena as alterações de forma total e
// igual em todos os aparelhos e respeita a causalidade mesmo com relógios errados: quem já viu uma alteração escreve
// sempre «depois» dela, ainda que o seu relógio esteja atrasado.
public readonly record struct Hlc(long Wall, int Counter, string Node) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, string.Empty);

    public int CompareTo(Hlc other)
    {
        var c = Wall.CompareTo(other.Wall);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Node, other.Node);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Wall:D13}-{Counter:D4}-{Node}";

    public static Hlc Parse(string text)
    {
        var parts = text.Split('-', 3);
        if (parts.Length != 3) throw new FormatException("HLC inválido: " + text);
        return new Hlc(long.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]);
    }
}

public sealed class HybridClock(string node, Func<long> nowMs)
{
    private long _wall;
    private int _counter;

    public string Node { get; } = node;

    public Hlc Last => new(_wall, _counter, Node);

    public Hlc Now()
    {
        var physical = nowMs();
        if (physical > _wall) { _wall = physical; _counter = 0; }
        else _counter++;
        return new Hlc(_wall, _counter, Node);
    }

    // Depois de ver uma alteração, as seguintes deste aparelho ficam sempre depois dela (HLC puro, sem cortar relógios
    // adiantados: cortar quebraria a causalidade e uma edição posterior podia perder para a que veio antes).
    public void Observe(Hlc remote)
    {
        if (remote.Wall > _wall) { _wall = remote.Wall; _counter = remote.Counter; }
        else if (remote.Wall == _wall) _counter = Math.Max(_counter, remote.Counter);
    }

    // Repõe o relógio guardado (o estado do aparelho entre arranques).
    public void Restore(Hlc last)
    {
        if (last > Last) { _wall = last.Wall; _counter = last.Counter; }
    }
}
