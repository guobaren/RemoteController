[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$VMName,
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$DestinationPath = 'C:\Temp\RemoteController\publish',
    [string]$UiUser = 'test'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Copying files through Hyper-V Guest Service Interface requires an elevated PowerShell session on the host.'
    }
}

Assert-Administrator

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$packageFiles = @(
    'Rc.Agent.exe',
    'Rc.PrivilegedBroker.exe',
    'Rc.TaskHost.exe',
    'Rc.UiAgent.exe',
    'Rc.UiTestApp.exe',
    'Rc.InteractiveTestApp.exe',
    'Rc.Cli.exe',
    'Install-RemoteController.ps1',
    'Update-RemoteController.ps1',
    'Uninstall-RemoteController.ps1',
    'Start-RemoteController.cmd',
    'Start-RemoteControllerUiTest.cmd',
    'Test-RemoteControllerUi.ps1',
    'Repair-RemoteControllerTlsIdentity.ps1',
    'Setup-RemoteControllerAgent.ps1',
    'Setup-RemoteControllerAgent.cmd',
    'RemoteController.Agent.config.json'
)

foreach ($file in $packageFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "Missing deployment package file: $file"
    }
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne 'Running') {
    throw "VM '$VMName' must be running before files can be copied through Guest Service Interface. Current state: $($vm.State)."
}

for ($index = 0; $index -lt $packageFiles.Count; $index++) {
    $file = $packageFiles[$index]
    $sourceFile = Join-Path $source $file
    $destinationFile = Join-Path $DestinationPath $file
    $percent = [Math]::Floor((100 * $index) / $packageFiles.Count)

    $progress = @{
        Activity = 'Copying RemoteController deployment package to Hyper-V guest'
        Status = "[$($index + 1)/$($packageFiles.Count)] $file"
        PercentComplete = $percent
    }
    Write-Progress @progress

    if ($PSCmdlet.ShouldProcess("${VMName}:$destinationFile", "Copy $file")) {
        Copy-VMFile -Name $VMName -SourcePath $sourceFile -DestinationPath $destinationFile -FileSource Host -CreateFullPath -Force -ErrorAction Stop
    }
}

Write-Progress -Activity 'Copying RemoteController deployment package to Hyper-V guest' -Completed

$agentHash = (Get-FileHash -LiteralPath (Join-Path $source 'Rc.Agent.exe') -Algorithm SHA256).Hash
Write-Host "[OK] Copied $($packageFiles.Count) deployment files to ${VMName}:$DestinationPath"
Write-Host "Expected Rc.Agent.exe SHA256: $agentHash"
Write-Host ''
Write-Host 'Run the following in an elevated PowerShell session inside the VM:'
Write-Host "Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force"
Write-Host ("& '{0}\Install-RemoteController.ps1' -SourcePath '{0}' -InstallPath 'C:\Program Files\RemoteController' -UiUser `"`$env:COMPUTERNAME\{1}`"" -f $DestinationPath, $UiUser)
