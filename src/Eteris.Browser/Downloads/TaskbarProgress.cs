using System.Windows.Shell;

namespace Eteris.Browser.Downloads;

// Progresso das transferências no ícone da barra de tarefas.
public static class TaskbarProgress
{
    public static (TaskbarItemProgressState State, double Value) For(bool hasActive, int percent)
        => !hasActive ? (TaskbarItemProgressState.None, 0)
            : percent <= 0 ? (TaskbarItemProgressState.Indeterminate, 0)
            : (TaskbarItemProgressState.Normal, percent / 100.0);

    // Quando a última transferência acaba com o Éteris em segundo plano, o ícone pisca para avisar.
    public static bool ShouldFlash(bool wasActive, bool isActive, bool windowFocused) => wasActive && !isActive && !windowFocused;
}
