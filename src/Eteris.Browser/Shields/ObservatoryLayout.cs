namespace Eteris.Browser.Shields;

// Um domínio no desenho do Observatório: posição no quadrado do gráfico, raio e a empresa (índice do grupo).
public sealed record PlacedNode(DomainStat Domain, string Company, int Group, double X, double Y, double Radius, double Angle);

// Etiqueta de uma empresa, no ângulo médio dos seus domínios, por fora do anel.
public sealed record PlacedCompany(string Name, int Group, double X, double Y, double Angle, int Domains);

public sealed record ObservatoryDrawing(double Size, double Center, IReadOnlyList<PlacedNode> Nodes, IReadOnlyList<PlacedCompany> Companies,
    int Hidden);

// Disposição do Observatório: o site ao centro e os domínios num anel à volta, as empresas em setores seguidos (com
// um intervalo entre elas) e, com muitos domínios, em dois anéis alternados para os nós não se tocarem. Só depende do
// que a página contactou — a mesma página dá sempre o mesmo desenho, e um domínio novo não baralha os outros grupos.
public static class ObservatoryLayout
{
    public const int MaxNodes = 48;
    public const double MinRadius = 5;
    public const double MaxRadius = 12;

    // Raio do nó: cresce com os pedidos (escala logarítmica), entre MinRadius e MaxRadius.
    public static double RadiusFor(int requests) => Math.Min(MaxRadius, MinRadius + 2.2 * Math.Log2(1 + Math.Max(0, requests)));

    public static ObservatoryDrawing Place(IReadOnlyList<CompanyGroup> groups, double size, int maxNodes = MaxNodes)
    {
        var center = size / 2;
        var outer = center - 46;
        var inner = outer * 0.68;
        var placed = new List<(DomainStat Domain, string Company, int Group, int IndexInGroup)>();
        var total = groups.Sum(g => g.Domains.Count);
        for (var g = 0; g < groups.Count && placed.Count < maxNodes; g++)
            for (var i = 0; i < groups[g].Domains.Count && placed.Count < maxNodes; i++)
                placed.Add((groups[g].Domains[i], groups[g].Name, g, i));

        var usedGroups = placed.Select(p => p.Group).Distinct().Count();
        var slots = placed.Count + (usedGroups > 1 ? usedGroups : 0);
        var step = slots == 0 ? 0 : 2 * Math.PI / slots;
        var twoRings = placed.Count > 14;
        var nodes = new List<PlacedNode>(placed.Count);
        var slot = 0;
        var lastGroup = -1;
        foreach (var p in placed)
        {
            if (p.Group != lastGroup && lastGroup >= 0 && usedGroups > 1) slot++;
            lastGroup = p.Group;
            var angle = -Math.PI / 2 + slot * step;
            var ring = !twoRings ? outer * 0.86 : (slot % 2 == 0 ? outer : inner);
            nodes.Add(new PlacedNode(p.Domain, p.Company, p.Group, center + ring * Math.Cos(angle), center + ring * Math.Sin(angle),
                RadiusFor(p.Domain.Requests), angle));
            slot++;
        }

        var companies = nodes.GroupBy(n => n.Group).Select(group =>
        {
            // Ângulo médio pelo vetor (um setor que passa pelo topo não dá a média errada).
            var angle = Math.Atan2(group.Sum(n => Math.Sin(n.Angle)), group.Sum(n => Math.Cos(n.Angle)));
            var r = outer + 26;
            return new PlacedCompany(group.First().Company, group.Key, center + r * Math.Cos(angle), center + r * Math.Sin(angle), angle, group.Count());
        }).ToList();
        return new ObservatoryDrawing(size, center, nodes, companies, total - nodes.Count);
    }
}
