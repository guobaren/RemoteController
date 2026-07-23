[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourcePath,
    [string]$InstallPath = (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'RemoteController'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'RemoteController'),
    [ValidateRange(1, 65535)][int]$TcpPort = 43001,
    [string]$UiUser,
    [switch]$NoFirewallRule
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The installer can run from either the source tree or a copied publish package.
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $packageAgent = Join-Path $PSScriptRoot 'Rc.Agent.exe'
    $SourcePath = if (Test-Path -LiteralPath $packageAgent -PathType Leaf) {
        $PSScriptRoot
    } else {
        Join-Path $PSScriptRoot '..\artifacts\publish'
    }
}

$agentService = 'RemoteControllerAgent'
$brokerService = 'RemoteControllerBroker'
$firewallRule = 'RemoteController Agent TCP'
$agentExe = Join-Path $InstallPath 'Rc.Agent.exe'
$brokerExe = Join-Path $InstallPath 'Rc.PrivilegedBroker.exe'
$taskHostExe = Join-Path $InstallPath 'Rc.TaskHost.exe'
$uiAgentExe = Join-Path $InstallPath 'Rc.UiAgent.exe'
$secretPath = Join-Path $DataRoot 'broker-auth.key'
$uiTaskName = 'RemoteControllerUiAgent'

if ([string]::IsNullOrWhiteSpace($UiUser)) {
    $existingTask = Get-ScheduledTask -TaskName $uiTaskName -ErrorAction SilentlyContinue
    $UiUser = if ($null -ne $existingTask -and -not [string]::IsNullOrWhiteSpace($existingTask.Principal.UserId)) {
        $existingTask.Principal.UserId
    } else {
        [Security.Principal.WindowsIdentity]::GetCurrent().Name
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Installation requires an elevated PowerShell session.' }
}
function Invoke-Sc([string]$Arguments) {
    $process = Start-Process -FilePath "$env:SystemRoot\System32\sc.exe" -ArgumentList $Arguments -NoNewWindow -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "sc.exe failed with exit code $($process.ExitCode): $Arguments" }
}
function Set-ServiceEnvironment([string]$ServiceName, [string[]]$Entries) {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if ($PSCmdlet.ShouldProcess($path, 'Configure service environment')) {
        New-ItemProperty -LiteralPath $path -Name Environment -PropertyType MultiString -Value $Entries -Force | Out-Null
    }
}

if (-not $PSBoundParameters.ContainsKey('WhatIf')) { Assert-Administrator }
$source = (Resolve-Path -LiteralPath $SourcePath).Path
foreach ($file in @('Rc.Agent.exe', 'Rc.PrivilegedBroker.exe', 'Rc.TaskHost.exe', 'Rc.UiAgent.exe', 'Rc.UiTestApp.exe', 'Rc.InteractiveTestApp.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing required publish artifact: $file" }
}
function Stop-ManagedService([string]$ServiceName) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) { return }

    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}
function Stop-UiAgent {
    $task = Get-ScheduledTask -TaskName $uiTaskName -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $uiTaskName
    }

    # Task Scheduler can report the task stopped before its desktop process exits.
    # Stop only this product's interactive executables so an update can replace them.
    foreach ($process in @(Get-Process -Name 'Rc.UiAgent', 'Rc.UiTestApp' -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $processes = @(Get-Process -Name 'Rc.UiAgent', 'Rc.UiTestApp' -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'RemoteController UI processes did not stop within 30 seconds.'
}

if ($PSCmdlet.ShouldProcess("$agentService, $brokerService", 'Stop services before replacing binaries')) {
    Stop-ManagedService $agentService
    Stop-ManagedService $brokerService
}
if ($PSCmdlet.ShouldProcess($uiTaskName, 'Stop UiAgent before replacing binaries')) {
    Stop-UiAgent
}

if ($PSCmdlet.ShouldProcess($InstallPath, 'Install RemoteController binaries')) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $InstallPath -Recurse -Force
}

if ($PSCmdlet.ShouldProcess("$agentService, $brokerService", 'Create or update Windows services')) {
    $brokerBinaryPath = '\"{0}\" --service' -f $brokerExe
    $agentBinaryPath = '\"{0}\" --service' -f $agentExe
    $brokerCreate = 'create {0} binPath= "{1}" start= auto obj= LocalSystem DisplayName= "{2}"' -f $brokerService, $brokerBinaryPath, 'RemoteController Privileged Broker'
    $brokerConfig = 'config {0} binPath= "{1}" start= auto obj= LocalSystem DisplayName= "{2}"' -f $brokerService, $brokerBinaryPath, 'RemoteController Privileged Broker'
    $agentCreate = 'create {0} binPath= "{1}" start= auto obj= "NT AUTHORITY\LocalService" DisplayName= "{2}"' -f $agentService, $agentBinaryPath, 'RemoteController Agent'
    $agentConfig = 'config {0} binPath= "{1}" start= auto obj= "NT AUTHORITY\LocalService" DisplayName= "{2}"' -f $agentService, $agentBinaryPath, 'RemoteController Agent'

    Invoke-Sc $(if (Get-Service -Name $brokerService -ErrorAction SilentlyContinue) { $brokerConfig } else { $brokerCreate })
    Invoke-Sc $(if (Get-Service -Name $agentService -ErrorAction SilentlyContinue) { $agentConfig } else { $agentCreate })
    Invoke-Sc ('sidtype {0} unrestricted' -f $agentService)
    Invoke-Sc ('sidtype {0} unrestricted' -f $brokerService)
    Invoke-Sc ('failure {0} reset= 86400 actions= restart/5000/restart/15000/restart/60000' -f $agentService)
    Invoke-Sc ('failureflag {0} 1' -f $agentService)
    Invoke-Sc ('failure {0} reset= 86400 actions= restart/5000/restart/15000/restart/60000' -f $brokerService)
    Invoke-Sc ('failureflag {0} 1' -f $brokerService)
    Invoke-Sc ('config {0} depend= {1}' -f $agentService, $brokerService)
}

$agentAccountSid = ([Security.Principal.NTAccount]'NT AUTHORITY\LOCAL SERVICE').Translate([Security.Principal.SecurityIdentifier]).Value
$brokerAccountSid = ([Security.Principal.NTAccount]'NT AUTHORITY\SYSTEM').Translate([Security.Principal.SecurityIdentifier]).Value
$uiUserSid = ([Security.Principal.NTAccount]$UiUser).Translate([Security.Principal.SecurityIdentifier]).Value
$uiLauncherPath = Join-Path $InstallPath 'Rc.UiAgentLauncher.vbs'
$escapedUiAgentExe = $uiAgentExe.Replace('"', '""')
$escapedAgentAccountSid = $agentAccountSid.Replace('"', '""')
$uiLauncherContent = @"
Option Explicit
Dim shell
Set shell = CreateObject("WScript.Shell")
shell.Environment("Process")("RC_UI_AGENT_CONTROL_CLIENT_SID") = "$escapedAgentAccountSid"
shell.Run Chr(34) & "$escapedUiAgentExe" & Chr(34) & " run", 0, False
"@
if ($PSCmdlet.ShouldProcess($uiLauncherPath, 'Create hidden UiAgent launcher')) {
    [IO.File]::WriteAllText($uiLauncherPath, $uiLauncherContent, [Text.Encoding]::ASCII)
}
if ($PSCmdlet.ShouldProcess($DataRoot, 'Create and secure service data directory')) {
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    & "$env:SystemRoot\System32\icacls.exe" $DataRoot '/inheritance:r' '/grant:r' "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)F" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls.exe failed with exit code $LASTEXITCODE" }
}
Set-ServiceEnvironment $agentService @(
    "RC_AGENT_DATA_ROOT=$DataRoot", "RC_AGENT_TCP_PORT=$TcpPort", "RC_TASKHOST_PATH=$taskHostExe",
    "RC_BROKER_SECRET_PATH=$secretPath", "RC_AGENT_TRUSTED_SIDS=$brokerAccountSid", "RC_UI_AGENT_CLIENT_SID=$uiUserSid"
)
Set-ServiceEnvironment $brokerService @(
    "RC_AGENT_DATA_ROOT=$DataRoot", "RC_BROKER_ALLOWED_DATA_ROOT=$DataRoot",
    "RC_BROKER_SECRET_PATH=$secretPath", "RC_BROKER_CLIENT_SID=$agentAccountSid"
)
if ($PSCmdlet.ShouldProcess($uiTaskName, "Create or update UI Agent logon task for $UiUser")) {
    # Use wscript.exe so the console application's standard output has no visible host window.
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\wscript.exe" -Argument "//B //Nologo `"$uiLauncherPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $UiUser
    $principal = New-ScheduledTaskPrincipal -UserId $UiUser -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $uiTaskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
}
if (-not $NoFirewallRule -and $PSCmdlet.ShouldProcess($firewallRule, "Allow inbound TCP $TcpPort")) {
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $TcpPort -Program $agentExe -Profile Private,Domain | Out-Null
}
if ($PSCmdlet.ShouldProcess($brokerService, 'Start service')) { Start-Service -Name $brokerService }
if ($PSCmdlet.ShouldProcess($agentService, 'Start service')) { Start-Service -Name $agentService }
if ($PSCmdlet.ShouldProcess($uiTaskName, 'Start UI Agent for an active interactive session')) {
    try {
        Start-ScheduledTask -TaskName $uiTaskName -ErrorAction Stop
    }
    catch {
        Write-Verbose "UiAgent task was registered but could not start immediately: $($_.Exception.Message)"
    }
}
Write-Host "RemoteController services installed. Agent TCP port: $TcpPort"
