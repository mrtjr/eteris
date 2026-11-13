using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Eteris.Browser.Infrastructure;

// Registo local das falhas inesperadas (INF-08, UIX-15): um ficheiro na pasta do perfil que o utilizador sabe encontrar
// (Definições › Sobre diz onde). Só o tipo e a pilha: a mensagem de uma exceção pode trazer endereços e caminhos, também de
// janelas privadas, por isso nunca se grava. Fica só nas últimas MaxEntries. Nada daqui sai do computador.
public static class CrashLog
{
    public const string FileName = "falhas.log";
    public const int MaxEntries = 20;
    private const int MaxDepth = 8;
    private static readonly object Gate = new();
    private static readonly Regex EntryStart = new("^## ", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // «## <UTC> <versão> <origem> <tipo>», a pilha, e uma linha «-> <tipo>» com a pilha de cada exceção interior.
    public static string Format(DateTime utc, string version, string source, Exception ex)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"## {utc.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ} {version} {source} {ex.GetType().FullName}\n");
        AppendStack(text, ex);
        AppendInner(text, ex, 1);
        return text.ToString();
    }

    private static void AppendInner(StringBuilder text, Exception ex, int depth)
    {
        if (depth > MaxDepth) return;
        IEnumerable<Exception> inner = ex is AggregateException aggregate ? aggregate.InnerExceptions
            : ex.InnerException is { } one ? [one] : [];
        foreach (var child in inner)
        {
            text.Append(CultureInfo.InvariantCulture, $"-> {child.GetType().FullName}\n");
            AppendStack(text, child);
            AppendInner(text, child, depth + 1);
        }
    }

    private static void AppendStack(StringBuilder text, Exception ex)
    {
        if (ex.StackTrace is { Length: > 0 } stack) text.Append(stack.Replace("\r\n", "\n")).Append('\n');
    }

    // Acrescenta a entrada e fica com as últimas MaxEntries (as entradas separam-se pelas linhas que começam por «## »);
    // por um ficheiro temporário e troca. Nunca lança: um registo que não se consegue gravar não pode ser mais uma falha.
    public static void Write(string file, string entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var entries = Entries(Read(file));
                entries.Add(entry.EndsWith('\n') ? entry : entry + "\n");
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
                AtomicFile.WriteText(file, string.Concat(entries), backup: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    public static int Count(string file)
    {
        try { return EntryStart.Matches(Read(file)).Count; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }

    // No máximo um aviso por minuto: um erro que se repete (um temporizador, uma tecla premida) não enche a janela de avisos.
    public static bool ShouldNotify(DateTime? last, DateTime now) => last is null || now - last >= TimeSpan.FromMinutes(1);

    private static string Read(string file) => File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";

    // Só as entradas que começam por «## »: o que vier antes (um ficheiro estranho) não se guarda.
    private static List<string> Entries(string text)
    {
        var starts = EntryStart.Matches(text).Select(m => m.Index).ToList();
        var entries = new List<string>(starts.Count + 1);
        for (var i = 0; i < starts.Count; i++)
        {
            var piece = text[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : text.Length)];
            entries.Add(piece.EndsWith('\n') ? piece : piece + "\n");
        }
        return entries;
    }

    // Os tratadores globais, ligados antes de haver janela. Num erro da interface com uma janela já carregada, o Éteris
    // continua aberto e avisa (no máximo uma vez por minuto); sem janela carregada o erro segue o caminho de sempre (o
    // arranque trata-o no App.OnStartup). Os outros dois só gravam: o AppDomain já vai terminar, e uma tarefa sem dono
    // fica observada em vez de derrubar o processo.
    public static void Install(Application app, string file, Action<string> notify)
    {
        var version = Version;
        DateTime? lastNotice = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(file, Format(DateTime.UtcNow, version, "interface", e.Exception));
            if (!app.Windows.OfType<MainWindow>().Any(w => w.IsLoaded)) return;
            e.Handled = true;
            if (!ShouldNotify(lastNotice, DateTime.UtcNow)) return;
            lastNotice = DateTime.UtcNow;
            try { notify("O Éteris recuperou de um erro. Os detalhes ficaram em Definições › Sobre."); }
            catch (Exception) { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(file, Format(DateTime.UtcNow, version, "processo", ex));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(file, Format(DateTime.UtcNow, version, "tarefa", e.Exception));
            e.SetObserved();
        };
    }

    // Grava uma falha de arranque tratada pelo App.OnStartup.
    public static void WriteStartup(string file, Exception ex) => Write(file, Format(DateTime.UtcNow, Version, "arranque", ex));

    private static string Version
        => typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
