[CmdletBinding()]
param(
    [string]$SourcePath = $PSScriptRoot,
    [string]$InstallPath = (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'RemoteController'),
    [string]$UiUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'TLS identity repair requires an elevated PowerShell session.'
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$installer = Join-Path $source 'Install-RemoteController.ps1'
$agentExe = Join-Path $InstallPath 'Rc.Agent.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing installer: $installer" }

Write-Progress -Activity 'Repairing RemoteController TLS identity' -Status 'Installing the complete deployment package' -PercentComplete 20
& $installer -SourcePath $source -InstallPath $InstallPath -UiUser $UiUser

if (-not (Test-Path -LiteralPath $agentExe -PathType Leaf)) { throw "Missing installed Agent: $agentExe" }

Write-Progress -Activity 'Repairing RemoteController TLS identity' -Status 'Requesting guarded TLS identity regeneration' -PercentComplete 60
& $agentExe repair-tls-identity
if ($LASTEXITCODE -ne 0) { throw "Agent repair request failed with exit code $LASTEXITCODE." }

Write-Progress -Activity 'Repairing RemoteController TLS identity' -Status 'Restarting the Agent service' -PercentComplete 80
Restart-Service -Name RemoteControllerAgent -Force
(Get-Service -Name RemoteControllerAgent).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))

Write-Progress -Activity 'Repairing RemoteController TLS identity' -Completed
Write-Host 'RemoteController Agent TLS identity was regenerated. Run Rc.Agent.exe identity locally and pin the new fingerprint on the controller.'
