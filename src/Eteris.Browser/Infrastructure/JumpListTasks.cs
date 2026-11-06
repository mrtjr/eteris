using System.Windows;
using System.Windows.Shell;

namespace Eteris.Browser.Infrastructure;

public sealed record JumpTaskInfo(string Title, string Arguments, string Description, int IconIndex);

// Lista de atalhos do ícone na barra de tarefas. Cada tarefa relança o exe; a instância aberta recebe os argumentos.
public static class JumpListTasks
{
    public static readonly IReadOnlyList<JumpTaskInfo> All =
    [
        new("Nova aba", "--new-tab", "Abrir uma nova aba no Éteris", 0),
        new("Nova janela privada", "--private", "Nada fica no histórico, nos cookies nem na cache", 0),
        new("Histórico", "eteris://history/", "Ver e pesquisar páginas visitadas", 0),
        new("Definições", "eteris://settings/", "Motor de pesquisa, aspeto e privacidade", 0),
    ];

    public static void Install()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        foreach (var task in All)
            list.JumpItems.Add(new JumpTask
            {
                Title = task.Title,
                Arguments = task.Arguments,
                Description = task.Description,
                ApplicationPath = exe,
                IconResourcePath = exe,
                IconResourceIndex = task.IconIndex,
            });
        try { JumpList.SetJumpList(Application.Current, list); } catch (Exception) { }
    }
}
