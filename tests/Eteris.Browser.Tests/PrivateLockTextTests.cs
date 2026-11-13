using System.Reflection;
using Eteris.Browser.Tabs;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests;

// Os textos da tranca das janelas privadas (Tabs/PrivateLockText.cs): o cartão não diz nada da página, todos dizem com
// honestidade que é uma camada de conveniência e não uma cifra, e o registo é formal, sem pronome.
public class PrivateLockTextTests
{
    private static IEnumerable<string> AllTexts()
    {
        foreach (var field in typeof(PrivateLockText).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is string text) yield return text;
        foreach (var availability in Enum.GetValues<ReauthAvailability>())
        {
            yield return PrivateLockText.CardLine(availability);
            yield return PrivateLockText.UnlockButton(availability);
            foreach (var count in new[] { 1, 2, 5 }) yield return PrivateLockText.LockedToast(count, availability);
        }
        foreach (var result in Enum.GetValues<PrivateUnlockResult>())
            if (PrivateLockText.Status(result) is { } status) yield return status;
        foreach (var mode in Enum.GetValues<PrivateUnlockTestMode>()) yield return PrivateLockText.TestMarker(mode);
    }

    [Fact]
    public void TheCard_NamesNothingFromThePage()
    {
        foreach (var text in AllTexts())
        {
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("%s", text);
        }
        foreach (var name in new[] { nameof(PrivateLockText.CardLine), nameof(PrivateLockText.UnlockButton), nameof(PrivateLockText.Status), nameof(PrivateLockText.LockedToast) })
        {
            var methods = typeof(PrivateLockText).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name).ToList();
            Assert.NotEmpty(methods);
            Assert.All(methods.SelectMany(m => m.GetParameters()), p =>
            {
                Assert.NotEqual(typeof(string), p.ParameterType);
                Assert.NotEqual(typeof(BrowserTab), p.ParameterType);
            });
        }
        Assert.Equal("Janela privada trancada", PrivateLockText.CardTitle);
    }

    [Fact]
    public void TheTexts_AreHonest()
    {
        Assert.Contains("camada de conveniência", PrivateLockText.Honest);
        Assert.Contains("não uma cifra", PrivateLockText.Honest);
        Assert.Contains("não uma cifra", PrivateLockText.SettingsDetail);
        Assert.Contains("não é uma cifra", PrivateLockText.CommandDetail);
    }

    [Fact]
    public void FormalRegister()
    {
        foreach (var text in AllTexts())
        {
            var padded = " " + text + " ";
            foreach (var word in new[] { " tu ", " te ", "você", "teu", "tua", "seu navegador", "sempre" })
                Assert.False(padded.Contains(word, StringComparison.OrdinalIgnoreCase), $"«{word.Trim()}» em «{text}»");
        }
    }

    [Fact]
    public void WindowTitle_IsNeutral() => Assert.Equal("Janela privada trancada — Éteris", PrivateLockText.WindowTitle);

    [Fact]
    public void HelloOrPassword()
    {
        Assert.Equal("Desbloquear com o Windows Hello", PrivateLockText.UnlockButton(ReauthAvailability.Hello));
        Assert.Equal("Desbloquear com a palavra-passe do Windows", PrivateLockText.UnlockButton(ReauthAvailability.WindowsPassword));
        Assert.Equal("Desbloqueie com o Windows Hello para ver as abas.", PrivateLockText.CardLine(ReauthAvailability.Hello));
        Assert.Equal("Desbloqueie com a palavra-passe do Windows para ver as abas.", PrivateLockText.CardLine(ReauthAvailability.WindowsPassword));
        Assert.Equal("A confirmar com o Windows…", PrivateLockText.Confirming);
        Assert.Equal("Continua trancada: a confirmação foi cancelada.", PrivateLockText.Status(PrivateUnlockResult.Canceled));
        Assert.Equal("O Windows não confirmou. Tente outra vez.", PrivateLockText.Status(PrivateUnlockResult.Failed));
        Assert.Equal("Já há uma confirmação do Windows aberta. Termine-a e tente outra vez.", PrivateLockText.Status(PrivateUnlockResult.Busy));
        Assert.Null(PrivateLockText.Status(PrivateUnlockResult.Interrupted));
        Assert.Null(PrivateLockText.Status(PrivateUnlockResult.Unlocked));
    }

    // O aviso numa janela normal depois de «Trancar janelas privadas» (Ctrl+Alt+L, paleta, menu ⋯).
    [Fact]
    public void TheToast_CountsTheWindows()
    {
        Assert.Equal("1 janela privada trancada — desbloqueia com o Windows Hello", PrivateLockText.LockedToast(1, ReauthAvailability.Hello));
        Assert.Equal("3 janelas privadas trancadas — desbloqueiam com o Windows Hello", PrivateLockText.LockedToast(3, ReauthAvailability.Hello));
        Assert.Equal("2 janelas privadas trancadas — desbloqueiam com a palavra-passe do Windows", PrivateLockText.LockedToast(2, ReauthAvailability.WindowsPassword));
    }

    // A marca que a prova ao vivo lê antes de clicar: só com o verificador de teste.
    [Fact]
    public void TheTestMarker_SaysWhichTestPath()
    {
        Assert.Equal("via de teste: cancelado", PrivateLockText.TestMarker(PrivateUnlockTestMode.Cancel));
        Assert.Equal("via de teste: indisponível", PrivateLockText.TestMarker(PrivateUnlockTestMode.Unavailable));
    }
}
