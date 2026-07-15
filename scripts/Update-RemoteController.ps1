[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [string]$InstallPath = (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'RemoteController'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'RemoteController'),
    [ValidateRange(1, 65535)][int]$TcpPort = 43001,
    [string]$UiUser,
    [switch]$NoFirewallRule
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $PSBoundParameters.ContainsKey('WhatIf') -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Updating RemoteController requires an elevated PowerShell session.'
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$installer = Join-Path $source 'Install-RemoteController.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing installer: $installer" }

$parent = Split-Path -Parent $InstallPath
$leaf = Split-Path -Leaf $InstallPath
$backupPath = Join-Path $parent (".$leaf.update-backup-" + [guid]::NewGuid().ToString('N'))
$installArguments = @{
    SourcePath = $source
    InstallPath = $InstallPath
    DataRoot = $DataRoot
    TcpPort = $TcpPort
}
if ($PSBoundParameters.ContainsKey('UiUser')) { $installArguments.UiUser = $UiUser }
if ($NoFirewallRule) { $installArguments.NoFirewallRule = $true }

function Stop-RemoteControllerServices {
    foreach ($serviceName in @('RemoteControllerAgent', 'RemoteControllerBroker')) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }
    }
}

function Stop-RemoteControllerUi {
    $task = Get-ScheduledTask -TaskName 'RemoteControllerUiAgent' -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName 'RemoteControllerUiAgent' -ErrorAction SilentlyContinue
    }
    Get-Process -Name 'Rc.UiAgent', 'Rc.UiTestApp' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Start-RemoteControllerServices {
    foreach ($serviceName in @('RemoteControllerBroker', 'RemoteControllerAgent')) {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
    }
    Start-ScheduledTask -TaskName 'RemoteControllerUiAgent' -ErrorAction SilentlyContinue
}

if (-not $PSCmdlet.ShouldProcess($InstallPath, 'Update RemoteController with rollback protection')) { return }

try {
    Stop-RemoteControllerUi
    Stop-RemoteControllerServices
    if (Test-Path -LiteralPath $InstallPath -PathType Container) {
        Move-Item -LiteralPath $InstallPath -Destination $backupPath -Force
    }
    & $installer @installArguments
    if (Test-Path -LiteralPath $backupPath -PathType Container) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }
}
catch {
    $originalError = $_
    Stop-RemoteControllerServices
    if (Test-Path -LiteralPath $InstallPath -PathType Container) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $backupPath -PathType Container) {
        Move-Item -LiteralPath $backupPath -Destination $InstallPath -Force
    }
    Start-RemoteControllerServices
    throw $originalError
}
