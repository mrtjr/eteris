<#
  Marca do Éteris no bootstrap da CEF (passo E de docs/sandbox.md): ícone e informação de versão no Eteris.Browser.exe,
  que hoje mostra «CEF Bootstrap Application» (Explorador, Gestor de Tarefas, avisos do Windows).
  Compila um .rc com o rc.exe do Windows SDK e troca, numa CÓPIA do bootstrap, os recursos RT_ICON, RT_GROUP_ICON e
  RT_VERSION pelos do .res (UpdateResource). O manifesto e o resto do binário ficam iguais. Antes de assinar.
  Uso: brand-bootstrap.ps1 -Exe <Eteris.Browser.exe> -Version 0.2.9 [-Icon <ico>] [-Rc <rc.exe>]
#>
param([Parameter(Mandatory)] [string]$Exe, [Parameter(Mandatory)] [string]$Version,
      [string]$Icon = '',
      [string]$Rc = '')
$ErrorActionPreference = 'Stop'
# O $PSScriptRoot fica vazio nos valores por omissão do param() no Windows PowerShell 5.1.
if (-not $Icon) { $Icon = Join-Path $PSScriptRoot '..\..\src\Eteris.Browser\Assets\eteris.ico' }
$Exe = [IO.Path]::GetFullPath($Exe); $Icon = [IO.Path]::GetFullPath($Icon)
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "versão inválida: $Version (esperado x.y.z)" }
if (-not $Rc) {
  $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
  $Rc = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\rc.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if (-not $Rc) { throw 'rc.exe do Windows SDK não encontrado (passe -Rc)' }
}
$v = $Version.Split('.'); $comma = "$($v[0]),$($v[1]),$($v[2]),0"
$work = Join-Path ([IO.Path]::GetTempPath()) ("eteris-brand-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  Copy-Item -LiteralPath $Icon -Destination (Join-Path $work 'eteris.ico')
  # UTF-16 com BOM: o rc.exe lê assim os acentos de «Éteris». Sem #include (o rc.exe corre sem o ambiente do VS):
  # 0x40004 = VOS_NT_WINDOWS32, 0x1 = VFT_APP.
  $rcText = @"
1 ICON "eteris.ico"
1 VERSIONINFO
FILEVERSION $comma
PRODUCTVERSION $comma
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "Éteris"
      VALUE "FileDescription", "Éteris"
      VALUE "FileVersion", "$Version"
      VALUE "InternalName", "Eteris.Browser"
      VALUE "OriginalFilename", "Eteris.Browser.exe"
      VALUE "ProductName", "Éteris"
      VALUE "ProductVersion", "$Version"
      VALUE "Comments", "Arranque do Éteris pelo bootstrap da CEF (BSD), com o sandbox dos processos das páginas."
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
  [IO.File]::WriteAllText((Join-Path $work 'brand.rc'), $rcText, (New-Object Text.UnicodeEncoding($false, $true)))
  Push-Location -LiteralPath $work
  try { & $Rc /nologo /fo brand.res brand.rc; if ($LASTEXITCODE -ne 0) { throw "rc.exe falhou ($LASTEXITCODE)" } } finally { Pop-Location }

  if (-not ('EterisBrand.Res' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
namespace EterisBrand {
public static class Res {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr BeginUpdateResourceW(string f, bool del);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateResourceW(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool EndUpdateResourceW(IntPtr h, bool discard);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryExW(string f, IntPtr h, uint flags);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr m);
  delegate bool EnumName(IntPtr m, IntPtr type, IntPtr name, IntPtr p);
  delegate bool EnumLang(IntPtr m, IntPtr type, IntPtr name, ushort lang, IntPtr p);
  [DllImport("kernel32.dll")] static extern bool EnumResourceNamesW(IntPtr m, IntPtr type, EnumName cb, IntPtr p);
  [DllImport("kernel32.dll")] static extern bool EnumResourceLanguagesW(IntPtr m, IntPtr type, IntPtr name, EnumLang cb, IntPtr p);

  // Só recursos com ID numérico (os do bootstrap e os do .rc acima).
  static List<Tuple<int, int, ushort>> Existing(string exe, int[] types) {
    var found = new List<Tuple<int, int, ushort>>();
    IntPtr m = LoadLibraryExW(exe, IntPtr.Zero, 0x22);
    if (m == IntPtr.Zero) throw new IOException("não abre como recurso: " + exe);
    try {
      foreach (int t in types) {
        EnumResourceNamesW(m, (IntPtr)t, (mm, ty, name, p) => {
          if (((long)name >> 16) != 0) throw new InvalidOperationException("recurso com nome de texto no tipo " + t);
          EnumResourceLanguagesW(mm, ty, name, (m2, t2, n2, lang, p2) => { found.Add(Tuple.Create(t, (int)(long)n2, lang)); return true; }, IntPtr.Zero);
          return true;
        }, IntPtr.Zero);
      }
    } finally { FreeLibrary(m); }
    return found;
  }

  public static string Apply(string exe, string res, int[] types) {
    var removed = Existing(exe, types);
    var added = new List<Tuple<int, int, ushort, byte[]>>();
    byte[] b = File.ReadAllBytes(res);
    int pos = 0;
    while (pos + 8 <= b.Length) {
      int dataSize = BitConverter.ToInt32(b, pos), headerSize = BitConverter.ToInt32(b, pos + 4);
      int q = pos + 8;
      if (BitConverter.ToUInt16(b, q) != 0xFFFF) throw new InvalidOperationException(".res com tipo em texto");
      int type = BitConverter.ToUInt16(b, q + 2); q += 4;
      if (BitConverter.ToUInt16(b, q) != 0xFFFF) throw new InvalidOperationException(".res com nome em texto");
      int name = BitConverter.ToUInt16(b, q + 2); q += 4;
      q = (q + 3) & ~3;
      ushort lang = BitConverter.ToUInt16(b, q + 6);
      if (dataSize > 0 && Array.IndexOf(types, type) >= 0) {
        var data = new byte[dataSize]; Array.Copy(b, pos + headerSize, data, 0, dataSize);
        added.Add(Tuple.Create(type, name, lang, data));
      }
      pos = (pos + headerSize + dataSize + 3) & ~3;
    }
    IntPtr h = BeginUpdateResourceW(exe, false);
    if (h == IntPtr.Zero) throw new IOException("BeginUpdateResource: " + Marshal.GetLastWin32Error());
    bool ok = false;
    try {
      foreach (var r in removed)
        if (!UpdateResourceW(h, (IntPtr)r.Item1, (IntPtr)r.Item2, r.Item3, null, 0)) throw new IOException("remover: " + Marshal.GetLastWin32Error());
      foreach (var a in added)
        if (!UpdateResourceW(h, (IntPtr)a.Item1, (IntPtr)a.Item2, a.Item3, a.Item4, (uint)a.Item4.Length)) throw new IOException("acrescentar: " + Marshal.GetLastWin32Error());
      ok = true;
    } finally {
      if (!EndUpdateResourceW(h, !ok) && ok) throw new IOException("EndUpdateResource: " + Marshal.GetLastWin32Error());
    }
    return "removidos " + removed.Count + ", acrescentados " + added.Count;
  }
}
}
'@
  }
  # 3 = RT_ICON, 14 = RT_GROUP_ICON, 16 = RT_VERSION
  $result = [EterisBrand.Res]::Apply($Exe, (Join-Path $work 'brand.res'), [int[]](3, 14, 16))
  $info = (Get-Item -LiteralPath $Exe).VersionInfo
  if ($info.ProductName -ne 'Éteris' -or $info.FileVersion -ne $Version) { throw "versão não aplicada: $($info.ProductName) $($info.FileVersion)" }
  "OK: $Exe ($result) - $($info.ProductName) $($info.FileVersion)"
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
