param([string]$ConfigPath = (Join-Path $PSScriptRoot 'interactive-open-count.config.json'))

$configuration = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$configuration.openCount = [int]$configuration.openCount + 1
$configuration | ConvertTo-Json | Set-Content -LiteralPath $ConfigPath -Encoding utf8
$openedCount = [int]$configuration.openCount
Write-Output ('This program has been opened ' + $openedCount + ' time(s).')
Write-Output 'Enter the number of times it has been opened, then press Enter:'
$enteredValue = [Console]::In.ReadLine()

if ([int]$enteredValue -ne $openedCount) {
    [Console]::Error.WriteLine('Incorrect count.')
    exit 2
}

Write-Output ('Count confirmed: ' + $openedCount)
