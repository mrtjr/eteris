using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// O aviso de sessão expirada (0.2.15, T11): «uma vez por abertura». Política pura, sem janelas nem sincronização.
public class SessionExpiredPromptTests
{
    [Fact]
    public void Prompt_FirstExpiry_AsksOnce()
    {
        var prompt = new SessionExpiredPrompt();
        Assert.False(prompt.Observe(false));
        Assert.False(prompt.Pending);
        Assert.True(prompt.Observe(true));
        Assert.True(prompt.Pending);
        // A ronda seguinte volta a dizer que é preciso entrar: não se pergunta outra vez, mas continua por responder.
        Assert.False(prompt.Observe(true));
        Assert.True(prompt.Pending);
    }

    [Fact]
    public void Prompt_NotNow_DoesNotComeBackThisRun()
    {
        var prompt = new SessionExpiredPrompt();
        Assert.True(prompt.Observe(true));
        prompt.Dismiss();
        Assert.False(prompt.Pending);
        Assert.False(prompt.Observe(false));
        Assert.False(prompt.Observe(true));
        Assert.False(prompt.Pending);
    }

    [Fact]
    public void Prompt_SessionRestored_ThenExpiresAgain_StaysQuiet()
    {
        var prompt = new SessionExpiredPrompt();
        Assert.True(prompt.Observe(true));
        Assert.True(prompt.Pending);
        // A sessão voltou por outro caminho (Definições, menu) sem resposta à barra: o aviso acaba (R2, EM ABERTO 3)...
        Assert.False(prompt.Observe(false));
        Assert.False(prompt.Pending);
        // ...e uma nova expiração na mesma abertura fica calada e sem barra.
        Assert.False(prompt.Observe(true));
        Assert.False(prompt.Pending);
    }

    [Fact]
    public void Prompt_NeverExpired_NeverPending()
    {
        var prompt = new SessionExpiredPrompt();
        for (var i = 0; i < 5; i++) Assert.False(prompt.Observe(false));
        Assert.False(prompt.Pending);
        // Responder sem aviso (a T12 fá-lo sempre ao sair) não faz nada: uma expiração mais tarde ainda avisa.
        prompt.Dismiss();
        Assert.False(prompt.Pending);
        Assert.True(prompt.Observe(true));
        Assert.True(prompt.Pending);
    }

    [Fact]
    public void Prompt_ManyThreads_AsksExactlyOnce()
    {
        var prompt = new SessionExpiredPrompt();
        var asked = 0;
        Parallel.For(0, 64, _ =>
        {
            if (prompt.Observe(true)) Interlocked.Increment(ref asked);
        });
        Assert.Equal(1, asked);
        Assert.True(prompt.Pending);
    }
}
