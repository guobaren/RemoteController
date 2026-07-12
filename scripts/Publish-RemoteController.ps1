[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }))
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }
$projects = @{
    'Rc.Agent' = 'src\Rc.Agent\Rc.Agent.csproj'
    'Rc.PrivilegedBroker' = 'src\Rc.PrivilegedBroker\Rc.PrivilegedBroker.csproj'
    'Rc.TaskHost' = 'src\Rc.TaskHost\Rc.TaskHost.csproj'
    'Rc.UiAgent' = 'src\Rc.UiAgent\Rc.UiAgent.csproj'
    'Rc.Cli' = 'src\Rc.Cli\Rc.Cli.csproj'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($name in $projects.Keys) {
    $project = Join-Path $root $projects[$name]
    $staging = Join-Path $output $name
    & $dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $staging
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name." }
    $executable = Join-Path $staging "$name.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Publish output for $name has no executable." }
    Copy-Item -LiteralPath $executable -Destination (Join-Path $output "$name.exe") -Force
}

foreach ($name in $projects.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $output "$name.exe") -PathType Leaf)) { throw "Missing packaged executable: $name.exe" }
}
Write-Host "Self-contained Windows x64 publish complete: $output"
