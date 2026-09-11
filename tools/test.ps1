<#
.SYNOPSIS
  Runs the Synaptic Sea test suites: the engine-free Core suite under dotnet, then Unity EditMode (and PlayMode).

.DESCRIPTION
  1. dotnet test on tools/dotnet/SynapticSea.Core.Tests (Core sources + EditMode tests, seconds, no Unity).
  2. Unity EditMode tests via the Unity CLI (`unity test`), NUnit XML under builds/logs.
  3. PlayMode when -Mode All or PlayMode.
  Exits non-zero on the first failing suite. The Unity project must not be open in another Editor.

.EXAMPLE
  pwsh tools/test.ps1
  pwsh tools/test.ps1 -Mode All
  pwsh tools/test.ps1 -Mode Dotnet
#>
param(
    [ValidateSet('EditMode', 'PlayMode', 'All', 'Dotnet')]
    [string]$Mode = 'EditMode',
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'SynapticSea'
$logs = Join-Path $repo 'builds\logs'
New-Item -ItemType Directory -Force $logs | Out-Null

$dotnet = if (Test-Path 'F:\Tools\dotnet\dotnet.exe') { 'F:\Tools\dotnet\dotnet.exe' } else { 'dotnet' }
$unity = if (Test-Path "$env:LOCALAPPDATA\Unity\bin\unity.exe") { "$env:LOCALAPPDATA\Unity\bin\unity.exe" } else { 'unity' }

function Summarize([string]$xmlPath, [string]$label) {
    [xml]$doc = Get-Content $xmlPath
    $run = $doc.'test-run'
    Write-Host ("{0}: total={1} passed={2} failed={3} skipped={4}" -f $label, $run.total, $run.passed, $run.failed, $run.skipped)
    $doc.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
        Write-Host ("  FAIL {0}: {1}" -f $_.fullname, ($_.failure.message.'#cdata-section' -split "`n")[0])
    }
    return [int]$run.failed
}

Write-Output '== dotnet (Core + EditMode sources)'
$dotnetArgs = @('test', (Join-Path $repo 'tools\dotnet\SynapticSea.Core.Tests'), '--nologo', '--logger', 'console;verbosity=minimal')
if ($Filter) { $dotnetArgs += @('--filter', "FullyQualifiedName~$Filter") }
& $dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { Write-Output 'TESTS FAIL suite=dotnet'; exit 1 }
if ($Mode -eq 'Dotnet') { Write-Output 'TESTS PASS suites=dotnet'; exit 0 }

$modes = switch ($Mode) { 'All' { @('EditMode', 'PlayMode') } default { @($Mode) } }
foreach ($m in $modes) {
    Write-Output "== Unity $m"
    $out = Join-Path $logs ("unity-{0}.xml" -f $m.ToLowerInvariant())
    $unityArgs = @('test', $project, '--mode', $m, '--output', $out)
    if ($Filter) { $unityArgs += @('--filter', $Filter) }
    & $unity @unityArgs
    $code = $LASTEXITCODE
    if (Test-Path $out) { $failed = Summarize $out "Unity $m" } else { $failed = -1 }
    # Unity CLI: exit 8 = tests ran and some failed; other non-zero = infrastructure problem.
    if ($code -ne 0 -or $failed -ne 0) { Write-Output "TESTS FAIL suite=$m exit=$code"; exit 1 }
}
Write-Output ("TESTS PASS suites=dotnet,{0}" -f ($modes -join ','))
