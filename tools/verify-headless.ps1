<#
.SYNOPSIS
  Smoke-launches a built player headless and fails on any exception or error in its log, or on the wrong build kind.

.DESCRIPTION
  Runs builds/<Target>/<Kind>/<player> with -batchmode -nographics for a few seconds (the player has no -quit of its
  own, so it is stopped after -Seconds), then:
    - requires AppServices' boot line to report the built kind ("[AppServices] composed ... build=<Kind>"), which proves
      build_stamp.json reached the player and the demo scope gate reads the right kind;
    - scans the player log for exceptions, errors and missing-script warnings.
  Windows players run directly. Linux players run inside WSL (default distribution Ubuntu, override with -WslDistro).
  Build first with tools/build.ps1.

.EXAMPLE
  pwsh tools/verify-headless.ps1
  pwsh tools/verify-headless.ps1 -Kind demo
  pwsh tools/verify-headless.ps1 -Target StandaloneLinux64 -Kind dev -Seconds 20
#>
param(
    [ValidateSet('StandaloneWindows64', 'StandaloneLinux64')]
    [string]$Target = 'StandaloneWindows64',
    [ValidateSet('dev', 'demo', 'release')]
    [string]$Kind = 'dev',
    [int]$Seconds = 10,
    [string]$WslDistro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$buildDir = Join-Path $repo "builds\$Target\$Kind"
$logs = Join-Path $repo 'builds\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$suffix = if ($Target -eq 'StandaloneWindows64') { $Kind } else { "linux-$Kind" }
$log = Join-Path $logs "player-headless-$suffix.log"
Remove-Item $log -ErrorAction SilentlyContinue

function ConvertTo-WslPath([string]$windowsPath) {
    $full = [System.IO.Path]::GetFullPath($windowsPath)
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    return "/mnt/$drive" + ($full.Substring(2) -replace '\\', '/')
}

if ($Target -eq 'StandaloneWindows64') {
    $exe = Join-Path $buildDir 'TheSynapticSea.exe'
    if (-not (Test-Path $exe)) { Write-Output "VERIFY FAIL missing $exe (run tools/build.ps1 -Kind $Kind)"; exit 1 }
    $proc = Start-Process -FilePath $exe -ArgumentList @('-batchmode', '-nographics', '-logFile', $log) -PassThru
    if (-not $proc.WaitForExit($Seconds * 1000)) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
}
else {
    $player = Join-Path $buildDir 'TheSynapticSea.x86_64'
    if (-not (Test-Path $player)) { Write-Output "VERIFY FAIL missing $player (run tools/build.ps1 -Target $Target -Kind $Kind)"; exit 1 }
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { Write-Output 'VERIFY FAIL wsl.exe not found (Linux players run under WSL)'; exit 1 }
    $wslDir = ConvertTo-WslPath $buildDir
    $wslLog = ConvertTo-WslPath $log
    # timeout sends SIGTERM after -Seconds (the player never quits by itself); exit 124 is the expected stop.
    $script = "cd '$wslDir' && chmod +x ./TheSynapticSea.x86_64 && timeout -k 5 $Seconds ./TheSynapticSea.x86_64 -batchmode -nographics -logFile '$wslLog'; code=`$?; echo wsl_exit=`$code"
    $wslOut = & wsl.exe -d $WslDistro -- bash -c $script 2>&1
    $wslOut | ForEach-Object { Write-Output "  wsl: $_" }
}
Start-Sleep -Milliseconds 300
if (-not (Test-Path $log)) { Write-Output "VERIFY FAIL player wrote no log ($log)"; exit 1 }

$problems = 0
$composed = Select-String -Path $log -Pattern '\[AppServices\] composed .*\bbuild=(\w+)' | Select-Object -First 1
if (-not $composed) {
    Write-Output '  no "[AppServices] composed" line: the player did not finish booting'
    $problems++
}
elseif ($composed.Matches[0].Groups[1].Value -ne $Kind) {
    Write-Output ("  build kind mismatch: player reports build={0}, expected {1}" -f $composed.Matches[0].Groups[1].Value, $Kind)
    $problems++
}

$patterns = @('Exception', '\bError\b', 'The referenced script .* is missing', 'NullReference', 'Failed to load')
$hits = Select-String -Path $log -Pattern $patterns | Where-Object { $_.Line -notmatch 'Fallback handler could not load library|d3d12' }
if ($hits) {
    $hits | Select-Object -First 20 | ForEach-Object { Write-Output ("  {0}: {1}" -f $_.LineNumber, $_.Line.Trim()) }
    $problems += @($hits).Count
}
if ($problems -gt 0) {
    Write-Output "VERIFY FAIL target=$Target kind=$Kind problems=$problems log=$log"
    exit 1
}
$lines = (Get-Content $log | Measure-Object -Line).Lines
$reported = $composed.Matches[0].Groups[1].Value
Write-Output "VERIFY PASS target=$Target kind=$Kind build=$reported log_lines=$lines log=$log"
