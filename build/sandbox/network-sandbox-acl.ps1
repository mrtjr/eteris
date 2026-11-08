# D9 de docs/sandbox.md: o Network Service com sandbox (funcionalidade NetworkServiceSandbox) corre num AppContainer,
# que só lê os ficheiros com uma entrada de acesso para as capacidades «chromeInstallFiles» e «lpacChromeInstallFiles».
# O instalador do Chrome dá-a à pasta de instalação (chrome/installer/setup/configure_app_container_sandbox.cc); sem
# ela o arranque falha («Sandbox cannot access executable … Acesso negado»), o Chromium grava
# net.network_service_failed_launch_major_version no Local State e o serviço de rede fica SEM sandbox até à próxima
# versão principal do motor.
#
# Uso: network-sandbox-acl.ps1 -Path <pasta da aplicação>            dá leitura+execução (herdada) às duas capacidades
#      network-sandbox-acl.ps1 -Path <pasta da aplicação> -Check     só verifica (código 0 = as duas entradas existem)
# Só mexe nas permissões da pasta indicada (nunca em configurações do sistema). Os SIDs são os do Chromium 156.
param([Parameter(Mandatory)][string]$Path, [switch]$Check)
$ErrorActionPreference = 'Stop'
$sids = @(
  'S-1-15-3-1024-3424233489-972189580-2057154623-747635277-1604371224-316187997-3786583170-1043257646',  # chromeInstallFiles
  'S-1-15-3-1024-2302894289-466761758-1166120688-1039016420-2430351297-4240214049-4028510897-3317428798'  # lpacChromeInstallFiles
)
$full = (Resolve-Path -LiteralPath $Path).Path
$acl = Get-Acl -LiteralPath $full
$present = @($sids | Where-Object { $sid = $_; @($acl.Access | Where-Object {
  $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $sid -and
  $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute }).Count })
if ($Check) {
  "$($present.Count)/2 entradas de AppContainer em $full"
  exit ([int]($present.Count -ne 2))
}
foreach ($sid in $sids) {
  $rule = New-Object Security.AccessControl.FileSystemAccessRule(
    (New-Object Security.Principal.SecurityIdentifier($sid)), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
  $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $full -AclObject $acl
"Leitura+execução herdada para chromeInstallFiles e lpacChromeInstallFiles em $full"
