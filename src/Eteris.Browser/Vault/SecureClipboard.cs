using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Eteris.Browser.Vault;

// A área de transferência do Windows, vista pelo cofre (a de verdade e a dos testes, que nunca tocam na real).
public interface IClipboardBackend
{
    // Muda sempre que alguém escreve ou esvazia a área de transferência.
    uint SequenceNumber { get; }

    // Texto e formatos extra numa só abertura; false se a área de transferência estava ocupada.
    bool SetText(string text, IReadOnlyList<(string Format, byte[] Data)> extra);

    bool Clear();
}

// Copiar um segredo (docs/palavras-passe.md, A5): no processo do navegador — a página nunca o vê —, com os formatos que
// o Windows respeita para o deixar fora do histórico (Win+V), da nuvem e de quem vigia a área de transferência; ao fim
// de 60 s, se ainda lá estiver (o número de sequência não mudou), é limpo. O que o utilizador copiar entretanto fica.
public sealed class SecureClipboard(IClipboardBackend backend, Func<TimeSpan, Action, IDisposable> schedule)
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<(string Format, byte[] Data)> ExclusionFormats { get; } =
    [
        ("ExcludeClipboardContentFromMonitorProcessing", new byte[4]),
        ("CanIncludeInClipboardHistory", new byte[4]),
        ("CanUploadToCloudClipboard", new byte[4]),
    ];

    private static readonly Lazy<SecureClipboard> s_shared = new(() => new SecureClipboard(new Win32Clipboard(), Timers.Schedule));

    public static SecureClipboard Shared => s_shared.Value;

    // As cópias por limpar (número de sequência de cada uma): ao fechar o Éteris limpam-se já, se ainda lá estiverem.
    private readonly HashSet<uint> _pending = [];

    public bool Copy(string secret)
    {
        if (!backend.SetText(secret, ExclusionFormats)) return false;
        var ours = backend.SequenceNumber;
        lock (_pending) _pending.Add(ours);
        schedule(ClearAfter, () =>
        {
            lock (_pending) _pending.Remove(ours);
            if (backend.SequenceNumber == ours) backend.Clear();
        });
        return true;
    }

    // O Éteris vai fechar antes dos 60 s: a palavra-passe copiada não fica na área de transferência (o que o utilizador
    // copiou depois fica).
    public void ClearPending()
    {
        bool ours;
        lock (_pending)
        {
            ours = _pending.Contains(backend.SequenceNumber);
            _pending.Clear();
        }
        if (ours) backend.Clear();
    }

    public static void ClearPendingOnExit()
    {
        if (s_shared.IsValueCreated) s_shared.Value.ClearPending();
    }
}

// Um segredo à vista: as janelas do Éteris ficam fora das capturas até ele se esconder ou, no máximo, 30 s (então
// «expired» avisa quem o mostrou para o esconder).
public sealed class SecretOnScreen : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly IDisposable _hold;
    private readonly IDisposable _timer;
    private readonly Action? _expired;
    private int _done;

    public SecretOnScreen(IDisposable hold, Func<TimeSpan, Action, IDisposable> schedule, Action? expired)
    {
        _hold = hold;
        _expired = expired;
        _timer = schedule(Limit, Expire);
    }

    public static SecretOnScreen Show(Action? expired = null) => new(Infrastructure.CaptureShield.HoldSecret(), Timers.Schedule, expired);

    private void Expire()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _hold.Dispose();
        _expired?.Invoke();
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (Interlocked.Exchange(ref _done, 1) == 0) _hold.Dispose();
    }
}

internal static class Timers
{
    // Os temporizadores ficam referenciados até disparar (senão o GC levava-os).
    private static readonly HashSet<Timer> Live = [];

    public static IDisposable Schedule(TimeSpan delay, Action run)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            lock (Live) Live.Remove(timer!);
            run();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (Live) Live.Add(timer);
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return new Cancel(timer);
    }

    private sealed class Cancel(Timer timer) : IDisposable
    {
        public void Dispose()
        {
            lock (Live) Live.Remove(timer);
            timer.Dispose();
        }
    }
}

// A área de transferência pela API do Windows. Abre-se com uma janela só de mensagens criada e destruída na mesma
// chamada (com dono NULL o SetClipboardData pode falhar; uma janela sem ciclo de mensagens que ficasse dona bloquearia
// quem esvaziasse a área de transferência depois). Os bytes do segredo em memória gerida são apagados a seguir.
internal sealed unsafe class Win32Clipboard : IClipboardBackend
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;
    private static readonly nint MessageOnly = -3;

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public bool SetText(string text, IReadOnlyList<(string Format, byte[] Data)> extra)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        try
        {
            return WithClipboard(() =>
            {
                if (!EmptyClipboard() || !Put(UnicodeText, bytes)) return false;
                foreach (var (format, data) in extra)
                {
                    var id = RegisterClipboardFormatW(format);
                    if (id == 0 || !Put(id, data)) return false;
                }
                return true;
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool Clear() => WithClipboard(EmptyClipboard);

    private static bool WithClipboard(Func<bool> work)
    {
        var window = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, MessageOnly, 0, 0, 0);
        try
        {
            var open = false;
            for (var attempt = 0; attempt < 10 && !(open = OpenClipboard(window)); attempt++) Thread.Sleep(20);
            if (!open) return false;
            try { return work(); }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (window != 0) DestroyWindow(window);
        }
    }

    private static bool Put(uint format, byte[] data)
    {
        var handle = GlobalAlloc(Moveable, (nuint)Math.Max(1, data.Length));
        if (handle == 0) return false;
        var target = GlobalLock(handle);
        if (target == 0)
        {
            GlobalFree(handle);
            return false;
        }
        data.AsSpan().CopyTo(new Span<byte>((void*)target, data.Length));
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != 0) return true;
        GlobalFree(handle);
        return false;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
