param([Parameter(Mandatory)][string]$Out, [string]$PackId = "EterisBrowserSbxTest4")
# Fotografia do estado do utilizador que uma instalação de teste do Velopack pode tocar, para comparar antes, depois de
# atualizar e depois de desinstalar (docs/sandbox.md, provas do Velopack; plano da 0.2.14, P10). Só lê; não muda nada.
#   - atalhos (Ambiente de Trabalho, menu Iniciar) com o destino e o SHA256, e os afixados na barra de tarefas;
#   - desinstalação, StartMenuInternet, RegisteredApplications, Classes com «eteris», UserChoice de http/https e de
#     .htm/.html/.pdf;
#   - perfis de AppContainer do Chromium (pastas cr.sb.* em %LOCALAPPDATA%\Packages e entradas cr.sb.* em
#     AppContainer\Mappings);
#   - a pasta da instalação de teste (%LOCALAPPDATA%\<PackId>).
$sh = New-Object -ComObject WScript.Shell
$taskbar = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$dirs = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'),
          [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('StartMenu'), $taskbar)
$lines = @()
foreach ($d in $dirs) {
  Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -Depth 1 -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
    $t = try { $sh.CreateShortcut($_.FullName).TargetPath } catch { '?' }
    $kind = if ($d -eq $taskbar) { 'TASKBAR' } else { 'LNK' }
    $lines += "$kind $($_.FullName) -> $t [$((Get-FileHash -LiteralPath $_.FullName).Hash.Substring(0,12))]"
  }
}
$lines += (Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | ForEach-Object { "UNINST $($_.PSChildName)" })
$lines += (Get-ChildItem 'HKCU:\Software\Clients\StartMenuInternet' -ErrorAction SilentlyContinue | ForEach-Object { "SMI $($_.PSChildName)" })
$ra = Get-ItemProperty 'HKCU:\Software\RegisteredApplications' -ErrorAction SilentlyContinue
if ($ra) { $lines += ($ra.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "REGAPP $($_.Name)=$($_.Value)" }) }
$lines += (Get-ChildItem 'HKCU:\Software\Classes' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match 'eteris' } | ForEach-Object { "CLASS $($_.PSChildName)" })
foreach ($p in 'http', 'https') { $lines += "USERCHOICE $p=$((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\$p\UserChoice" -ErrorAction SilentlyContinue).ProgId)" }
foreach ($e in '.htm', '.html', '.pdf') { $lines += "FILECHOICE $e=$((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$e\UserChoice" -ErrorAction SilentlyContinue).ProgId)" }
$lines += (Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'cr.sb.*' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "PACKAGES $($_.Name)" })
$map = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings'
$lines += (Get-ChildItem -LiteralPath $map -ErrorAction SilentlyContinue | ForEach-Object {
  $m = (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).Moniker
  if ($m -like 'cr.sb.*') { "MAPPING $m $($_.PSChildName)" } } | Sort-Object)
$lines += "DIR $PackId=$(Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA $PackId))"
$lines | Set-Content -LiteralPath $Out -Encoding utf8
"$($lines.Count) linhas em $Out"
