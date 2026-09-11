<#
.SYNOPSIS
  Smoke-launches a built Windows player headless and fails on any exception or error in its log.

.DESCRIPTION
  Runs builds/<Target>/<Kind>/TheSynapticSea.exe with -batchmode -nographics for a few seconds (the player has no
  -quit of its own, so it is stopped after -Seconds), then scans the player log for exceptions, errors and
  missing-script warnings. Build first with tools/build.ps1.

.EXAMPLE
  pwsh tools/verify-headless.ps1
  pwsh tools/verify-headless.ps1 -Kind release -Seconds 20
#>
param(
    [ValidateSet('dev', 'demo', 'release')]
    [string]$Kind = 'dev',
    [int]$Seconds = 10
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $repo "builds\StandaloneWindows64\$Kind\TheSynapticSea.exe"
if (-not (Test-Path $exe)) { Write-Output "VERIFY FAIL missing $exe (run tools/build.ps1 -Kind $Kind)"; exit 1 }
$logs = Join-Path $repo 'builds\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$log = Join-Path $logs "player-headless-$Kind.log"
Remove-Item $log -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $exe -ArgumentList @('-batchmode', '-nographics', '-logFile', $log) -PassThru
if (-not $proc.WaitForExit($Seconds * 1000)) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
Start-Sleep -Milliseconds 300
if (-not (Test-Path $log)) { Write-Output 'VERIFY FAIL player wrote no log'; exit 1 }

$patterns = @('Exception', '\bError\b', 'The referenced script .* is missing', 'NullReference', 'Failed to load')
$hits = Select-String -Path $log -Pattern $patterns | Where-Object { $_.Line -notmatch 'Fallback handler could not load library|d3d12' }
if ($hits) {
    $hits | Select-Object -First 20 | ForEach-Object { Write-Output ("  {0}: {1}" -f $_.LineNumber, $_.Line.Trim()) }
    Write-Output "VERIFY FAIL problems=$(@($hits).Count) log=$log"
    exit 1
}
$lines = (Get-Content $log | Measure-Object -Line).Lines
Write-Output "VERIFY PASS kind=$Kind log_lines=$lines log=$log"
