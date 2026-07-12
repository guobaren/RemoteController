[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'RemoteController'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'RemoteController'),
    [switch]$KeepData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$services = @('RemoteControllerAgent', 'RemoteControllerBroker')
$firewallRule = 'RemoteController Agent TCP'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $PSBoundParameters.ContainsKey('WhatIf') -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Uninstallation requires an elevated PowerShell session.' }

foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $PSCmdlet.ShouldProcess($name, 'Stop and delete Windows service')) {
        if ($service.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
        & "$env:SystemRoot\System32\sc.exe" delete $name | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not delete service $name" }
    }
}
if ($PSCmdlet.ShouldProcess($firewallRule, 'Remove firewall rule')) {
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
if (Test-Path -LiteralPath $InstallPath -PathType Container) {
    $resolvedInstall = (Resolve-Path -LiteralPath $InstallPath).Path
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
    if (-not $resolvedInstall.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove install path outside Program Files: $resolvedInstall" }
    if ($PSCmdlet.ShouldProcess($resolvedInstall, 'Remove installed binaries')) { Remove-Item -LiteralPath $resolvedInstall -Recurse -Force }
}
if (-not $KeepData -and (Test-Path -LiteralPath $DataRoot -PathType Container)) {
    $resolvedData = (Resolve-Path -LiteralPath $DataRoot).Path
    $programData = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
    if (-not $resolvedData.StartsWith($programData, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove data path outside ProgramData: $resolvedData" }
    if ($PSCmdlet.ShouldProcess($resolvedData, 'Remove RemoteController data')) { Remove-Item -LiteralPath $resolvedData -Recurse -Force }
}
Write-Host 'RemoteController services uninstalled.'
