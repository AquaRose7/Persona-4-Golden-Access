<#
.SYNOPSIS
  Show the tail of the most recent Reloaded II log and surface any exceptions.
.PARAMETER Tail
  Number of trailing lines to print (default 80).
.PARAMETER Full
  If set, prints the whole file instead of just the tail.
.EXAMPLE
  .\checklog.ps1              # last 80 lines of latest P4G Reloaded log
  .\checklog.ps1 -Tail 200    # last 200 lines
  .\checklog.ps1 -Full        # entire latest log
#>
param(
    [int]$Tail = 80,
    [switch]$Full
)

$logDir = Join-Path $env:APPDATA 'Reloaded-Mod-Loader-II\Logs'
if (-not (Test-Path $logDir)) { Write-Host "No Reloaded log directory at $logDir" -ForegroundColor Red; exit 1 }

$latest = Get-ChildItem $logDir -Filter '* ~ P4G.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) { Write-Host "No P4G logs found in $logDir" -ForegroundColor Yellow; exit 1 }

Write-Host "=== $($latest.Name) ($([int]((Get-Date) - $latest.LastWriteTime).TotalMinutes) min ago, $([int]($latest.Length/1kb)) KB) ===" -ForegroundColor Cyan

if ($Full) { Get-Content $latest.FullName } else { Get-Content $latest.FullName -Tail $Tail }

# Surface error-ish lines anywhere in the file.
$errLines = Select-String -Path $latest.FullName -Pattern 'Exception|Error|error|Crash|Unhandled|fatal' -CaseSensitive:$false
if ($errLines) {
    Write-Host "`n=== Error-like lines ($($errLines.Count)) ===" -ForegroundColor Yellow
    $errLines | ForEach-Object { "{0,5}: {1}" -f $_.LineNumber, $_.Line } | Select-Object -Last 40
}
