using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Browser;

// eteris://credits — os componentes de terceiros e as suas licenças (THIRD-PARTY-NOTICES.md, embebido).
public static partial class CreditsPage
{
    public const string Url = "eteris://credits/";

    private static readonly Lazy<string> Notices = new(() =>
    {
        using var stream = typeof(CreditsPage).Assembly.GetManifestResourceStream("Eteris.Browser.THIRD-PARTY-NOTICES.md");
        if (stream is null) return "# Componentes de terceiros\n\nVer THIRD-PARTY-NOTICES.md no repositório.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // Markdown mínimo e seguro: títulos, parágrafos, tabelas, `código` e endereços. Todo o texto é escapado.
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(Inline(string.Join(' ', paragraph))).Append("</p>\n");
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith('#'))
            {
                Flush();
                var level = Math.Min(line.TakeWhile(c => c == '#').Count(), 3);
                html.Append($"<h{level}>").Append(Inline(line.TrimStart('#').Trim())).Append($"</h{level}>\n");
                continue;
            }
            if (line.StartsWith('|'))
            {
                Flush();
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) continue;
                    rows.Add(cells);
                }
                i--;
                html.Append("<table>");
                for (var r = 0; r < rows.Count; r++)
                {
                    var tag = r == 0 ? "th" : "td";
                    html.Append("<tr>");
                    foreach (var cell in rows[r]) html.Append($"<{tag}>").Append(Inline(cell)).Append($"</{tag}>");
                    html.Append("</tr>");
                }
                html.Append("</table>\n");
                continue;
            }
            paragraph.Add(line);
        }
        Flush();
        return html.ToString();
    }

    private static string Inline(string text)
    {
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        escaped = CodeSpan().Replace(escaped, "<code>$1</code>");
        return Link().Replace(escaped, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
    }

    public static string Render() => """
        <!doctype html><html lang="pt"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
        <meta name="color-scheme" content="dark"><title>Créditos e licenças</title>
        <style>
          :root { --bg: #0b0d13; --text: #e9ebf2; --muted: #a3a9b8; --line: rgba(255,255,255,.08); --card: rgba(255,255,255,.03); }
          html.light { --bg: #f3f4f9; --text: #1b1f2a; --muted: #535b6e; --line: rgba(20,24,40,.1); --card: rgba(255,255,255,.85); }
          body { background: var(--bg); color: var(--text); margin: 0; font: 14px/1.6 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
          main { width: min(900px, 92vw); margin: 0 auto; padding: 50px 0 80px; }
          h1 { font-size: 32px; font-weight: 300; margin: 0 0 10px; }
          h2 { font-size: 13px; letter-spacing: .08em; text-transform: uppercase; color: var(--muted); margin: 34px 0 10px; }
          p { color: var(--muted); }
          table { width: 100%; border-collapse: collapse; background: var(--card); border: 1px solid var(--line); border-radius: 14px; overflow: hidden; }
          th, td { text-align: left; padding: 10px 14px; border-bottom: 1px solid var(--line); vertical-align: top; }
          th { font-size: 12px; color: var(--muted); font-weight: 600; }
          a { color: #22d3ee; text-decoration: none; word-break: break-all; }
          code { font: 12.5px Consolas, monospace; background: rgba(127,127,140,.15); padding: 1px 5px; border-radius: 5px; }
        </style></head><body><main>
        """ + ToHtml(Notices.Value) + "</main></body></html>";

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"https?://[^\s<|·]+")]
    private static partial Regex Link();
}
