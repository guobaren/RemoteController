[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'RemoteController'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'RemoteController'),
    [ValidateRange(1, 65535)][int]$TcpPort = 43001,
    [switch]$NoFirewallRule
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$agentService = 'RemoteControllerAgent'
$brokerService = 'RemoteControllerBroker'
$firewallRule = 'RemoteController Agent TCP'
$agentExe = Join-Path $InstallPath 'Rc.Agent.exe'
$brokerExe = Join-Path $InstallPath 'Rc.PrivilegedBroker.exe'
$taskHostExe = Join-Path $InstallPath 'Rc.TaskHost.exe'
$secretPath = Join-Path $DataRoot 'broker-auth.key'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Installation requires an elevated PowerShell session.' }
}
function Invoke-Sc([string[]]$Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')" }
}
function Set-ServiceEnvironment([string]$ServiceName, [string[]]$Entries) {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if ($PSCmdlet.ShouldProcess($path, 'Configure service environment')) {
        New-ItemProperty -LiteralPath $path -Name Environment -PropertyType MultiString -Value $Entries -Force | Out-Null
    }
}

if (-not $PSBoundParameters.ContainsKey('WhatIf')) { Assert-Administrator }
$source = (Resolve-Path -LiteralPath $SourcePath).Path
foreach ($file in @('Rc.Agent.exe', 'Rc.PrivilegedBroker.exe', 'Rc.TaskHost.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing required publish artifact: $file" }
}
if ($PSCmdlet.ShouldProcess($InstallPath, 'Install RemoteController binaries')) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $InstallPath -Recurse -Force
}

if ($PSCmdlet.ShouldProcess("$agentService, $brokerService", 'Create or update Windows services')) {
    if (Get-Service -Name $agentService -ErrorAction SilentlyContinue) { Stop-Service -Name $agentService -Force -ErrorAction SilentlyContinue }
    if (Get-Service -Name $brokerService -ErrorAction SilentlyContinue) { Stop-Service -Name $brokerService -Force -ErrorAction SilentlyContinue }
    if (Get-Service -Name $brokerService -ErrorAction SilentlyContinue) {
        Invoke-Sc @('config', $brokerService, "binPath= `"$brokerExe`" --service", 'start= auto', 'obj= LocalSystem', 'DisplayName= RemoteController Privileged Broker')
    } else {
        Invoke-Sc @('create', $brokerService, "binPath= `"$brokerExe`" --service", 'start= auto', 'obj= LocalSystem', 'DisplayName= RemoteController Privileged Broker')
    }
    if (Get-Service -Name $agentService -ErrorAction SilentlyContinue) {
        Invoke-Sc @('config', $agentService, "binPath= `"$agentExe`" --service", 'start= auto', 'obj= NT AUTHORITY\LocalService', 'DisplayName= RemoteController Agent')
    } else {
        Invoke-Sc @('create', $agentService, "binPath= `"$agentExe`" --service", 'start= auto', 'obj= NT AUTHORITY\LocalService', 'DisplayName= RemoteController Agent')
    }
    Invoke-Sc @('sidtype', $agentService, 'unrestricted')
    Invoke-Sc @('sidtype', $brokerService, 'unrestricted')
    Invoke-Sc @('failure', $agentService, 'reset= 86400', 'actions= restart/5000/restart/15000/restart/60000')
    Invoke-Sc @('failureflag', $agentService, '1')
    Invoke-Sc @('failure', $brokerService, 'reset= 86400', 'actions= restart/5000/restart/15000/restart/60000')
    Invoke-Sc @('failureflag', $brokerService, '1')
    Invoke-Sc @('config', $agentService, "depend= $brokerService")
}

$agentAccountSid = ([Security.Principal.NTAccount]'NT AUTHORITY\LOCAL SERVICE').Translate([Security.Principal.SecurityIdentifier]).Value
$brokerAccountSid = ([Security.Principal.NTAccount]'NT AUTHORITY\SYSTEM').Translate([Security.Principal.SecurityIdentifier]).Value
if ($PSCmdlet.ShouldProcess($DataRoot, 'Create and secure service data directory')) {
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    & "$env:SystemRoot\System32\icacls.exe" $DataRoot '/inheritance:r' '/grant:r' "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)F" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls.exe failed with exit code $LASTEXITCODE" }
}
Set-ServiceEnvironment $agentService @(
    "RC_AGENT_DATA_ROOT=$DataRoot", "RC_AGENT_TCP_PORT=$TcpPort", "RC_TASKHOST_PATH=$taskHostExe",
    "RC_BROKER_SECRET_PATH=$secretPath", "RC_AGENT_TRUSTED_SIDS=$brokerAccountSid"
)
Set-ServiceEnvironment $brokerService @(
    "RC_AGENT_DATA_ROOT=$DataRoot", "RC_BROKER_ALLOWED_DATA_ROOT=$DataRoot",
    "RC_BROKER_SECRET_PATH=$secretPath", "RC_BROKER_CLIENT_SID=$agentAccountSid"
)
if (-not $NoFirewallRule -and $PSCmdlet.ShouldProcess($firewallRule, "Allow inbound TCP $TcpPort")) {
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $TcpPort -Program $agentExe -Profile Private,Domain | Out-Null
}
if ($PSCmdlet.ShouldProcess($brokerService, 'Start service')) { Start-Service -Name $brokerService }
if ($PSCmdlet.ShouldProcess($agentService, 'Start service')) { Start-Service -Name $agentService }
Write-Host "RemoteController services installed. Agent TCP port: $TcpPort"
