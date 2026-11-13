using System.Windows.Input;
using Microsoft.Win32;

namespace Eteris.Browser.Tabs;

// Porque é que uma janela privada se tranca (MainWindow.PrivateLock.cs).
public enum PrivateLockReason
{
    // «Trancar janelas privadas» (Ctrl+Alt+L, paleta, menu ⋯): tranca mesmo sem a definição e só com abas novas.
    Manual,
    // O Windows bloqueou (Win+L), a consola ou a ligação remota desligou, ou terminou a sessão.
    WindowsLocked,
    // O computador vai suspender.
    Suspended,
    // A janela ficou parada o tempo escolhido nas Definições.
    Idle,
}

// A regra da tranca das janelas privadas, sem WPF: como no Safari, tranca ao bloquear o Windows, ao suspender e com a
// janela parada; uma janela só com abas novas não se tranca sozinha. A inatividade nunca tranca com som, vídeo ou uma
// chamada (câmara, microfone, ecrã) em curso: esconder o vídeo que se vê, ou a chamada, estragava o caso em que não há uso
// de teclado. O bloqueio do Windows e a suspensão trancam na mesma: a tranca não para nada, só esconde.
public static class PrivateLockPolicy
{
    // «Trancar depois de parada»: 0 = nunca (só ao bloquear o Windows e ao suspender).
    public static readonly int[] Choices = [0, 5, 15, 60];

    public const int DefaultMinutes = 15;

    public static bool IsChoice(int minutes) => Choices.Contains(minutes);

    // As mesmas razões em que o cofre esquece a confirmação (Vault/WindowsReauth.cs, ResetsOn).
    public static bool LocksOn(SessionSwitchReason reason)
        => reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.SessionLogoff;

    public static bool LocksOn(PowerModes mode) => mode == PowerModes.Suspend;

    // A tranca manual foi pedida: tranca-se na mesma. As automáticas precisam da definição ligada e de uma página que não
    // seja a nova aba.
    public static bool ShouldLock(PrivateLockReason reason, bool enabled, bool hasContent, bool alreadyLocked)
        => !alreadyLocked && (reason == PrivateLockReason.Manual || (enabled && hasContent));

    // A última vez que a janela esteve em uso (relógio monotónico): com a janela ativa, a última entrada do sistema; com
    // ela inativa, fica a anterior. Nunca anda para trás.
    public static TimeSpan LastUse(TimeSpan previous, bool active, TimeSpan now, TimeSpan lastInputAgo)
        => active && now - lastInputAgo > previous ? now - lastInputAgo : previous;

    // Há quanto tempo foi a última entrada (GetLastInputInfo, em tiques de 32 bits): a subtração sem sinal aguenta a volta
    // dos 49,7 dias.
    public static TimeSpan InputAgo(uint tickNow, uint lastInputTick) => TimeSpan.FromMilliseconds(unchecked(tickNow - lastInputTick));

    // O tempo parado que tranca: os minutos escolhidos, ou os segundos do gancho de teste ETERIS_TEST_TRANCA_PARADA; null com
    // «Nunca».
    public static TimeSpan? IdleLimit(int minutes, int? testSeconds)
        => minutes <= 0 ? null : testSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(minutes);

    public static bool IdleLockDue(int minutes, TimeSpan idle, bool playing, bool capturing)
        => IdleLockDue(IdleLimit(minutes, testSeconds: null), idle, playing, capturing);

    public static bool IdleLockDue(TimeSpan? limit, TimeSpan idle, bool playing, bool capturing)
        => limit is { } due && !playing && !capturing && idle >= due;

    // Ctrl+Alt+L, só com o Alt da esquerda: o AltGr (Ctrl+Alt da direita para o Windows) +L escreve «ł» no teclado polaco.
    public static bool IsLockKey(Key key, bool shift, bool leftAlt) => !shift && leftAlt && key == Key.L;
}
