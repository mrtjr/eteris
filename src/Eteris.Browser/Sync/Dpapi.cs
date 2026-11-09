using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Eteris.Sync.Google;

namespace Eteris.Browser.Sync;

// DPAPI do Windows (CryptProtectData, âmbito do utilizador) por P/Invoke, sem pacote: os tokens da Google e o
// estado da sincronização só se leem nesta conta do Windows, neste computador. A «entropia» separa os usos.
internal sealed class Dpapi(string purpose) : ISecretProtector
{
    private const int UiForbidden = 0x1;
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("Eteris.Sync:" + purpose);

    public byte[] Protect(byte[] data) => Run(data, protect: true);

    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private byte[] Run(byte[] data, bool protect)
    {
        var input = Blob.From(data);
        var entropy = Blob.From(_entropy);
        var output = default(Blob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Eteris", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            // A chave do cofre (e os tokens) passam por estas cópias nativas: apagadas antes de libertar.
            input.Free();
            entropy.Free();
            if (output.Data != IntPtr.Zero)
            {
                Zero(output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;

        public static Blob From(byte[] bytes)
        {
            var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public void Free()
        {
            if (Data == IntPtr.Zero) return;
            Zero(Data, Size);
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
        }
    }

    private static void Zero(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size <= 0) return;
        Marshal.Copy(new byte[size], 0, data, size);
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob dataIn, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
