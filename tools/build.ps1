<#
.SYNOPSIS
  Builds a Synaptic Sea player through Builder.PerformBuild.

.EXAMPLE
  pwsh tools/build.ps1                                   # Windows dev (Mono, development build)
  pwsh tools/build.ps1 -Kind release                     # Windows release (IL2CPP)
  pwsh tools/build.ps1 -Target StandaloneOSX -Kind demo  # macOS Mono (unsigned)
#>
param(
    [ValidateSet('StandaloneWindows64', 'StandaloneOSX', 'StandaloneLinux64')]
    [string]$Target = 'StandaloneWindows64',
    [ValidateSet('dev', 'demo', 'release')]
    [string]$Kind = 'dev'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'SynapticSea'
$out = Join-Path $repo "builds\$Target\$Kind"
$log = Join-Path $repo "builds\logs\build-$Target-$Kind.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
$editor = 'F:\Unity\6000.6.0f1\Editor\Unity.exe'

& $editor -batchmode -projectPath $project -buildTarget $Target `
    -executeMethod SynapticSea.EditorTools.Build.Builder.PerformBuild `
    -buildKind $Kind -outputPath $out -logFile $log | Out-Null
$code = $LASTEXITCODE
Select-String -Path $log -Pattern '\[Builder\]|error CS' | ForEach-Object { $_.Line }
if ($code -ne 0) { Write-Output "BUILD FAIL exit=$code log=$log"; exit 1 }
Write-Output "BUILD PASS out=$out"
