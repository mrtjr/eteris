<#
  Verificação da publicação com sandbox (docs/sandbox.md, D6), corrida pelo EterisSandboxCheck do csproj antes de
  compilar: a DLL cliente (build/sandbox/client) tem de ser da MESMA compilação do motor que o runtime publicado, e
  continuar a poder ser carregada em todos os processos filhos com sandbox. Falha (código 1) quando:
    - a versão da CEF gravada na DLL (recurso de versão: ProductVersion e «cef_commit=» em Comments, do cef_version.h da
      distribuição) não é a do manifest.json do runtime (cef.version / cef.commit) — uma DLL antiga depois de atualizar o
      motor publicaria sem erro e só falharia em execução;
    - a DLL importa diretamente algo além do KERNEL32 (o resto tem de ir por /DELAYLOAD) ou não carrega o libcef.dll
      em diferido;
    - exporta outra coisa além de RunWinMain e EterisSandboxInfo;
    - o bootstrap.exe do runtime ou a DLL trazem o recurso CEF_INSTALLER_CONFIG (instalador embutido da CEF, que
      baixaria a CEF oficial sem os codecs).
  Uso: verify-client.ps1 -Dll <Eteris.Browser.dll> -Runtime <build/cef-codecs/out/runtime>
#>
param([Parameter(Mandatory)] [string]$Dll, [Parameter(Mandatory)] [string]$Runtime)
$ErrorActionPreference = 'Stop'
$Dll = [IO.Path]::GetFullPath($Dll); $Runtime = [IO.Path]::GetFullPath($Runtime)
foreach ($f in $Dll, (Join-Path $Runtime 'manifest.json'), (Join-Path $Runtime 'bootstrap.exe')) {
  if (-not (Test-Path -LiteralPath $f)) { Write-Error "falta $f"; exit 1 }
}

if (-not ('EterisPe.Image' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace EterisPe {
public static class Image {
  static int RvaToOffset(byte[] b, int sections, int count, int rva) {
    for (int i = 0; i < count; i++) {
      int s = sections + i * 40;
      int va = BitConverter.ToInt32(b, s + 12), vsize = Math.Max(BitConverter.ToInt32(b, s + 8), BitConverter.ToInt32(b, s + 16));
      if (rva >= va && rva < va + vsize) return rva - va + BitConverter.ToInt32(b, s + 20);
    }
    throw new InvalidDataException("RVA fora das secções: " + rva);
  }
  static string Ascii(byte[] b, int offset) {
    int end = offset; while (b[end] != 0) end++;
    return Encoding.ASCII.GetString(b, offset, end - offset);
  }
  // Devolve [importações diretas, importações em diferido, exportações por nome] de um PE32+ (x64).
  public static List<string>[] Read(string path) {
    byte[] b = File.ReadAllBytes(path);
    int pe = BitConverter.ToInt32(b, 0x3C);
    if (BitConverter.ToUInt32(b, pe) != 0x4550) throw new InvalidDataException("não é PE: " + path);
    int coff = pe + 4, count = BitConverter.ToUInt16(b, coff + 2), optSize = BitConverter.ToUInt16(b, coff + 16);
    int opt = coff + 20;
    if (BitConverter.ToUInt16(b, opt) != 0x20B) throw new InvalidDataException("não é PE32+ (x64): " + path);
    int dirs = opt + 112, sections = opt + optSize;
    var imports = new List<string>(); var delayed = new List<string>(); var exports = new List<string>();
    int importRva = BitConverter.ToInt32(b, dirs + 1 * 8);
    if (importRva != 0)
      for (int d = RvaToOffset(b, sections, count, importRva); BitConverter.ToInt32(b, d + 12) != 0; d += 20)
        imports.Add(Ascii(b, RvaToOffset(b, sections, count, BitConverter.ToInt32(b, d + 12))));
    int delayRva = BitConverter.ToInt32(b, dirs + 13 * 8);
    if (delayRva != 0)
      for (int d = RvaToOffset(b, sections, count, delayRva); BitConverter.ToInt32(b, d + 4) != 0; d += 32)
        delayed.Add(Ascii(b, RvaToOffset(b, sections, count, BitConverter.ToInt32(b, d + 4))));
    int exportRva = BitConverter.ToInt32(b, dirs);
    if (exportRva != 0) {
      int e = RvaToOffset(b, sections, count, exportRva);
      int names = BitConverter.ToInt32(b, e + 24), table = RvaToOffset(b, sections, count, BitConverter.ToInt32(b, e + 32));
      for (int i = 0; i < names; i++) exports.Add(Ascii(b, RvaToOffset(b, sections, count, BitConverter.ToInt32(b, table + i * 4))));
    }
    return new[] { imports, delayed, exports };
  }

  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryExW(string f, IntPtr h, uint flags);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindResourceW(IntPtr m, string name, IntPtr type);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr m);
  // Recurso RCDATA com nome (o nome também aparece no código do bootstrap: procura-se o RECURSO, não o texto).
  public static bool HasRcData(string path, string name) {
    IntPtr m = LoadLibraryExW(path, IntPtr.Zero, 0x22);   // LOAD_LIBRARY_AS_DATAFILE | AS_IMAGE_RESOURCE
    if (m == IntPtr.Zero) throw new IOException("não abre como recurso: " + path);
    try { return FindResourceW(m, name, (IntPtr)10) != IntPtr.Zero; } finally { FreeLibrary(m); }
  }
}
}
'@
}

$problems = New-Object Collections.Generic.List[string]
$manifest = Get-Content -LiteralPath (Join-Path $Runtime 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$info = (Get-Item -LiteralPath $Dll).VersionInfo
$commit = if ($info.Comments -match 'cef_commit=([0-9a-f]{40})') { $Matches[1] } else { '' }
if (-not $commit) { $problems.Add("a DLL não tem a versão da CEF gravada (Comments: «$($info.Comments)»): compile-a com build/sandbox/client/build.cmd") }
elseif ($commit -ne $manifest.cef.commit) { $problems.Add("a DLL foi compilada com a CEF $commit, o runtime é da $($manifest.cef.commit) ($($manifest.cef.version)): recompile-a com a distribuição deste motor") }
# DLL de teste (cmake -DETERIS_TEST_HOOKS=ON: ganchos lidos do ambiente, docs/sandbox.md «Rede de segurança»): nunca publicar.
$bytes = [IO.File]::ReadAllBytes($Dll)
$hook = [Text.Encoding]::Unicode.GetString($bytes).Contains('ETERIS_TEST_')
if ($info.Comments -match 'test_hooks' -or $hook) { $problems.Add("é uma DLL de teste (ganchos ETERIS_TEST_*): compile-a sem -DETERIS_TEST_HOOKS") }
if ($info.ProductVersion -notlike "$($manifest.cef.version)+*") { $problems.Add("versão da CEF na DLL «$($info.ProductVersion)», no runtime $($manifest.cef.version)") }

$pe = [EterisPe.Image]::Read($Dll)
$direct = @($pe[0] | ForEach-Object { $_.ToUpperInvariant() })
$delayed = @($pe[1] | ForEach-Object { $_.ToUpperInvariant() })
$exports = @($pe[2] | Sort-Object)
if (($direct -join ',') -ne 'KERNEL32.DLL') { $problems.Add("importações diretas além do KERNEL32: $($pe[0] -join ', ') (o resto tem de ir por /DELAYLOAD)") }
if ($delayed -notcontains 'LIBCEF.DLL') { $problems.Add("o libcef.dll não está em carga diferida ($($pe[1] -join ', '))") }
if (($exports -join ',') -ne 'EterisSandboxInfo,RunWinMain') { $problems.Add("exportações inesperadas: $($exports -join ', ')") }
foreach ($f in (Join-Path $Runtime 'bootstrap.exe'), $Dll) {
  if ([EterisPe.Image]::HasRcData($f, 'CEF_INSTALLER_CONFIG')) { $problems.Add("$f tem o recurso CEF_INSTALLER_CONFIG") }
}

if ($problems.Count) {
  foreach ($p in $problems) { [Console]::Error.WriteLine("verify-client: $p") }
  exit 1
}
"OK: $Dll - CEF $($manifest.cef.version) ($($commit.Substring(0, 12))), importações diretas: $($pe[0] -join ', '); em diferido: $($pe[1] -join ', ')"
exit 0
