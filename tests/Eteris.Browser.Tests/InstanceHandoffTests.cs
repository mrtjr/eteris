using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// INF-05: um lançamento feito enquanto o Éteris anterior ainda fecha não se perde: espera até 10 s e abre no lugar dele.
public class InstanceHandoffTests
{
    private sealed class Counters
    {
        public int Sends, Pauses;
        public TimeSpan Elapsed;
    }

    private static (T? Instance, bool Delivered) Run<T>(Counters c, Func<int, T?> acquire, Func<bool> send) where T : class
    {
        var tries = 0;
        return InstanceHandoff.Run(() => acquire(tries++), () => { c.Sends++; return send(); }, () => c.Elapsed,
            pause => { c.Pauses++; c.Elapsed += TimeSpan.FromSeconds(1); Assert.Equal(InstanceHandoff.Pause, pause); });
    }

    [Fact]
    public void FirstInstance_IsPrimary_WithoutSending()
    {
        var c = new Counters();
        var o = new object();
        var (instance, delivered) = Run(c, _ => o, () => true);
        Assert.Same(o, instance);
        Assert.False(delivered);
        Assert.Equal(0, c.Sends);
    }

    [Fact]
    public void SecondInstance_Delivers()
    {
        var c = new Counters();
        var (instance, delivered) = Run<object>(c, _ => null, () => true);
        Assert.Null(instance);
        Assert.True(delivered);
        Assert.Equal(0, c.Pauses);
    }

    [Fact]
    public void OldInstanceClosing_BecomesPrimaryWhenTheMutexFrees()
    {
        var c = new Counters();
        var o = new object();
        var (instance, delivered) = Run(c, tries => tries >= 2 ? o : null, () => false);
        Assert.Same(o, instance);
        Assert.False(delivered);
        Assert.Equal(2, c.Pauses);
    }

    [Fact]
    public void NeverDeliveredNorFree_GivesUpAfterTenSeconds()
    {
        var c = new Counters();
        var (instance, delivered) = Run<object>(c, _ => null, () => false);
        Assert.Null(instance);
        Assert.False(delivered);
        Assert.Equal(10, c.Pauses);
    }

    [Fact]
    public void Inbox_KeepsMessagesUntilAttached_InOrder()
    {
        var inbox = new InstanceInbox();
        var got = new List<string>();
        inbox.Post(["a"]);
        inbox.Post(["b"]);
        inbox.Attach(args => got.Add(args[0]));
        inbox.Post(["c"]);
        Assert.Equal(["a", "b", "c"], got);
    }

    [Fact]
    public void Program_ListensRightAfterTheInstance_AndAppOnlyAttaches()
    {
        var main = GoogleSyncTests.Src("Eteris.Browser", "Program.cs");
        var listen = main.IndexOf("InstanceChannel.Listen(ChannelName, Inbox.Post)", StringComparison.Ordinal);
        Assert.True(listen > 0);
        Assert.True(listen < main.IndexOf("StartupTrace.Mark(\"instancia\");", StringComparison.Ordinal));
        Assert.Contains("InstanceHandoff.Run(", main);
        var app = GoogleSyncTests.Src("Eteris.Browser", "App.xaml.cs");
        Assert.DoesNotContain("InstanceChannel.Listen(", app);
        Assert.Contains("Program.Inbox.Attach(", app);
        Assert.Contains("Program.Channel?.Dispose();", app);
    }
}
