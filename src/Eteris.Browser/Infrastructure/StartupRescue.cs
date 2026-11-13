using System.Globalization;
using System.IO;
using System.Windows;

namespace Eteris.Browser.Infrastructure;

// Arranques falhados que não são os do sandbox (INF-04). A rede de segurança do sandbox (StartupGuard) só existe com
// sandbox possível; sem ela, um Éteris que cai ao arrancar caía de novo no arranque seguinte, para sempre, sem forma de ir
// buscar a versão que o corrige (o atualizador vive dentro dele). Aqui, numa instalação:
//   - Begin (no Program.Main, depois da instância única): conta mais um arranque no ficheiro do perfil;
//   - Healthy: a 1.ª página apareceu, ou fecho normal: apaga o ficheiro (a contagem volta a zero);
//   - com FailedStartsToRescue arranques seguidos sem uma coisa nem outra, antes do motor, pergunta se se procura já uma
//     versão corrigida (UpdateService.RescueAsync) e, se sim, instala-a e reabre.
// O marcador só se escreve depois da instância única: um 2.º lançamento sai depois de entregar os argumentos e contava como
// queda. Custa uma leitura e uma escrita de um ficheiro pequeno antes da 1.ª página, só numa instalação (exceção à regra
// R-2, a aceitar pelo dono depois de medida): o ficheiro lê-se e grava-se à mão, sem o JsonSerializer, cujo arranque em
// frio pesaria mais do que o próprio disco. Nunca lança: um ficheiro ilegível ou impossível de escrever conta zero.
public static class StartupRescue
{
    public const string FileName = "startup-rescue.json";

    // Arranques falhados seguidos a partir dos quais o seguinte oferece a versão corrigida.
    public const int FailedStartsToRescue = 2;

    private const string Prefix = "{\"Pending\":";

    private static string? _file;
    private static int _cleared;

    // Conta este arranque como «por acabar» e devolve quantos arranques seguidos antes dele ficaram assim (0 se o ficheiro
    // falta, está cortado ou não se lê). Grava por ficheiro temporário e troca: um corte a meio não deixa um ficheiro partido.
    public static int Begin(string file)
    {
        var before = Read(file);
        try
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, Prefix + (before + 1).ToString(CultureInfo.InvariantCulture) + "}");
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { File.Delete(file + ".tmp"); } catch (Exception) { /* nada a fazer */ }
        }
        return before;
    }

    // O arranque correu bem: apaga o ficheiro, sem lançar.
    public static void Clear(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { /* fica para o próximo */ }
    }

    public static bool ShouldOffer(int failedBefore, bool installed) => installed && failedBefore >= FailedStartsToRescue;

    // No Program.Main, depois da instância única e antes do motor. Fora de uma instalação não faz nada (nem ficheiro).
    public static void Start(string root, string[] args, bool installed)
    {
        if (!installed) return;
        _file = Path.Combine(root, FileName);
        var failed = Begin(_file);
        StartupTrace.Mark("rescue.marcador", "falhados=" + failed.ToString(CultureInfo.InvariantCulture));
        if (!ShouldOffer(failed, installed)) return;
        // A Application do WPF ainda não existe: a pergunta e a janela são feitas em código, sem recursos.
        var answer = MessageBox.Show(
            "O Éteris não conseguiu abrir das últimas " + FailedStartsToRescue.ToString(CultureInfo.InvariantCulture)
            + " vezes. Procurar agora uma versão corrigida e instalá-la? Com «Não», tenta abrir normalmente.",
            "Éteris", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) new Updates.RescueWindow(args).ShowDialog();
    }

    // A 1.ª página apareceu, ou fecho normal (App.OnExit, com now: true: o processo já está a sair). Uma só vez por
    // processo; sem o Start (não é uma instalação) não faz nada. Apaga fora do fio da interface.
    public static void Healthy(bool now = false)
    {
        if (_file is not { } file || Interlocked.Exchange(ref _cleared, 1) == 1) return;
        if (now) Clear(file);
        else _ = Task.Run(() => Clear(file));
    }

    // «{"Pending":N}», com N inteiro não negativo; qualquer outra coisa (em falta, cortado, negativo, enorme) é 0.
    private static int Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var text = File.ReadAllText(file).Trim();
            if (!text.StartsWith(Prefix, StringComparison.Ordinal) || !text.EndsWith('}')) return 0;
            var number = text[Prefix.Length..^1];
            return number.Length is > 0 and <= 9 && number.All(char.IsAsciiDigit)
                ? int.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }
}
