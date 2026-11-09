<#
  Procura do caminho do perfil de quem compila (o nome do utilizador do Windows) dentro de binários, para nenhum sair
  publicado com ele (docs/sandbox.md, «Caminhos de quem compila»). Usado com dot-source por build/publish.ps1 (a pasta
  publish\, antes da marca do fim) e por build/sandbox/build-all.ps1 (as peças nativas, logo a seguir a compilá-las,
  antes dos testes e da marca deles). Nada aqui tem o nome escrito: vem do %USERPROFILE% de quem corre o script.

    $scan = New-ProfileScan                        # $null sem USERPROFILE
    Test-ProfilePath $scan $bytes [-Pdb]           # $true se os bytes levam o caminho do perfil

  O que procura, nas formas longa e curta 8.3 do nome:
    - «\Users\<nome>\», «/Users/<nome>/» e «\\Users\\<nome>\\» (JSON, por exemplo o sourcelink.json), em UTF-8 e
      UTF-16LE, sem distinguir maiúsculas;
    - nos PDB portáteis (-Pdb), também o nome como segmento solto: o PDB portátil guarda os caminhos aos bocados, cada
      um com o comprimento antes (só para nomes com 4 bytes ou mais, para não dar falsos positivos);
    - o PDB portátil embutido nas montagens .NET (DebugType embedded, entrada de depuração 17: «MPDB», o tamanho em
      uint32 e os dados em deflate): os caminhos das fontes e o sourcelink.json vão lá comprimidos, e a procura direta
      não os vê (era o caso do CefSharp.Core.dll compilado pelo build-all.ps1 antes da arrumação 0.2.14).
#>

function New-ProfileScan {
  if (-not $env:USERPROFILE) { return $null }
  $profileDir = [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')
  $profileParent = Split-Path -Leaf (Split-Path -Parent $profileDir)
  $names = @(Split-Path -Leaf $profileDir)
  try { $names += Split-Path -Leaf ((New-Object -ComObject Scripting.FileSystemObject).GetFolder($profileDir).ShortPath) } catch { Write-Verbose 'Perfil sem nome curto 8.3' }
  $names = @($names | Where-Object { $_ } | Sort-Object -Unique)
  $latin1 = [Text.Encoding]::GetEncoding(28591)
  $needles = @(foreach ($name in $names) {
    foreach ($form in "\$profileParent\$name\", "/$profileParent/$name/", "\\$profileParent\\$name\\") {
      $latin1.GetString([Text.Encoding]::UTF8.GetBytes($form))
      $latin1.GetString([Text.Encoding]::Unicode.GetBytes($form))
    }
  })
  $segments = @(foreach ($name in $names) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($name)
    if ($bytes.Length -ge 4 -and $bytes.Length -lt 128) { $latin1.GetString([byte[]](@([byte]$bytes.Length) + $bytes)) }
  })
  New-Object PSObject -Property @{ Needles = $needles; Segments = $segments }
}

# Os bytes vistos como texto Latin-1 (um carácter por byte), para procurar as agulhas com IndexOf.
function Test-ProfileText($Scan, [string]$Text, [bool]$Pdb) {
  foreach ($needle in $Scan.Needles) { if ($Text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
  if ($Pdb) { foreach ($segment in $Scan.Segments) { if ($Text.IndexOf($segment, [StringComparison]::Ordinal) -ge 0) { return $true } } }
  $false
}

function Test-ProfilePath($Scan, [byte[]]$Bytes, [switch]$Pdb) {
  $latin1 = [Text.Encoding]::GetEncoding(28591)
  $text = $latin1.GetString($Bytes)
  if (Test-ProfileText $Scan $text $Pdb.IsPresent) { return $true }
  # PDB portátil embutido: «MPDB», tamanho descomprimido (uint32) e deflate. Um «MPDB» que não seja o cabeçalho dá
  # dados inválidos e salta-se.
  $at = $text.IndexOf('MPDB', [StringComparison]::Ordinal)
  while ($at -ge 0 -and $at + 8 -lt $Bytes.Length) {
    $size = [BitConverter]::ToInt32($Bytes, $at + 4)
    if ($size -gt 0 -and $size -le 256MB) {
      $compressed = [IO.MemoryStream]::new($Bytes, $at + 8, $Bytes.Length - $at - 8, $false)
      $deflate = [IO.Compression.DeflateStream]::new($compressed, [IO.Compression.CompressionMode]::Decompress)
      try {
        $pdbBytes = [byte[]]::new($size)
        $read = 0
        while ($read -lt $size) {
          $n = $deflate.Read($pdbBytes, $read, $size - $read)
          if ($n -le 0) { break }
          $read += $n
        }
        if ($read -eq $size -and (Test-ProfileText $Scan $latin1.GetString($pdbBytes, 0, $read) $true)) { return $true }
      } catch {
        Write-Verbose "«MPDB» na posição $at sem um PDB embutido válido"
      } finally { $deflate.Dispose() }
    }
    $at = $text.IndexOf('MPDB', $at + 4, [StringComparison]::Ordinal)
  }
  $false
}
