# Comum às provas ao vivo (lido com «. "$PSScriptRoot\comum.ps1"»). A pasta de trabalho guarda os perfis de teste, as
# capturas e o ficheiro PAUSA-UI; nunca é o perfil nem a instalação do utilizador.
#   -Work <pasta>, ou a variável ETERIS_PROVAS, ou %TEMP%\eteris-provas.
function Get-ProvasWork([string]$Work) {
  $dir = if ($Work) { $Work } elseif ($env:ETERIS_PROVAS) { $env:ETERIS_PROVAS } else { Join-Path $env:TEMP 'eteris-provas' }
  $dir = [IO.Path]::GetFullPath($dir)
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
  $dir
}

# PAUSA-UI na pasta de trabalho: o utilizador está a usar o PC (por exemplo um jogo) e as provas não abrem janelas nem
# enviam teclas ou cliques. O script sai com 3 e a verificação fica registada como pendente.
function Test-ProvasPausa([string]$Dir) {
  if (Test-Path -LiteralPath (Join-Path $Dir 'PAUSA-UI')) {
    "PAUSADO: o PC está em uso; provas ao vivo suspensas. Não abrir janelas nem enviar teclas ou cliques; registar a verificação ao vivo como pendente."
    exit 3
  }
}

# Instâncias do Éteris de teste abertas a partir desta pasta de trabalho (perfil <Work>\profile-*), menos a do perfil dado.
function Get-ProvasOthers([string]$Dir, [string]$Except = '') {
  # Pelo nome da pasta (o caminho pode chegar na forma longa ou na curta 8.3).
  $pattern = [regex]::Escape((Split-Path $Dir -Leaf)) + '\\(profile-[^"\s\\]+)'
  @(Get-CimInstance Win32_Process -Filter "Name='Eteris.Browser.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match $pattern -and $Matches[1] -ne $Except })
}
