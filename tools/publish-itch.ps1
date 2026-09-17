<#
.SYNOPSIS
  Stages a built player for itch.io, runs `butler validate` on it, and pushes only when asked.

.DESCRIPTION
  1. Reads the version from the player's own build_stamp.json (written by Builder.PerformBuild), so the itch user
     version always names the build that is actually uploaded.
  2. Copies builds/<Target>/<Kind> to builds/staging/<Target>-<Kind>, leaving out the folders Unity marks as
     not-for-shipping (*_BackUpThisFolder_ButDontShipItWithYourGame, *_BurstDebugInformation_DoNotShip).
  3. Runs `butler validate --platform <p> --arch amd64` on the staging folder. Exit 1 when it fails.
  4. Dry run by default: prints the exact `butler push` command. It pushes only with -Push AND -Project.

  Channels default to the store plan's names (Godot docs/game/store_requirements.md ITCH-017): win-rc, mac-rc,
  linux-rc, with a -demo suffix for demo builds. butler must be logged in (`butler login`, stored outside the repo)
  or have BUTLER_API_KEY set before -Push; this script never logs in.

.EXAMPLE
  pwsh tools/publish-itch.ps1                                   # validate the Windows release build, dry run
  pwsh tools/publish-itch.ps1 -Kind demo                        # channel win-rc-demo, dry run
  pwsh tools/publish-itch.ps1 -Target StandaloneLinux64 -Kind release -Project 9thlevelsoftware/the-synaptic-sea -Push
#>
param(
    [ValidateSet('StandaloneWindows64', 'StandaloneOSX', 'StandaloneLinux64')]
    [string]$Target = 'StandaloneWindows64',
    [ValidateSet('dev', 'demo', 'release')]
    [string]$Kind = 'release',
    [string]$Channel,
    # itch.io "user/game" slug. Required with -Push.
    [string]$Project,
    [switch]$Push,
    [string]$Butler = 'F:\Tools\butler\butler.exe'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$buildDir = Join-Path $repo "builds\$Target\$Kind"
$staging = Join-Path $repo "builds\staging\$Target-$Kind"

if (-not (Test-Path $buildDir)) { Write-Output "PUBLISH FAIL missing $buildDir (run tools/build.ps1 -Target $Target -Kind $Kind)"; exit 1 }
if (-not (Test-Path $Butler)) {
    $onPath = Get-Command butler -ErrorAction SilentlyContinue
    if (-not $onPath) { Write-Output "PUBLISH FAIL butler not found at $Butler or on PATH"; exit 1 }
    $Butler = $onPath.Source
}

$platform = switch ($Target) { 'StandaloneWindows64' { 'windows' } 'StandaloneOSX' { 'osx' } 'StandaloneLinux64' { 'linux' } }
if (-not $Channel) {
    $base = switch ($Target) { 'StandaloneWindows64' { 'win-rc' } 'StandaloneOSX' { 'mac-rc' } 'StandaloneLinux64' { 'linux-rc' } }
    $Channel = if ($Kind -eq 'demo') { "$base-demo" } else { $base }
}

# The stamp inside the player is the version the build reports at runtime (AppServices).
$stampRelative = switch ($Target) {
    'StandaloneOSX' { 'TheSynapticSea.app\Contents\Resources\Data\StreamingAssets\build_stamp.json' }
    default { 'TheSynapticSea_Data\StreamingAssets\build_stamp.json' }
}
$stampPath = Join-Path $buildDir $stampRelative
if (-not (Test-Path $stampPath)) { Write-Output "PUBLISH FAIL no build stamp at $stampPath (the player was not built by Builder.PerformBuild)"; exit 1 }
$stamp = Get-Content $stampPath -Raw | ConvertFrom-Json
if ($stamp.build_kind -ne $Kind) { Write-Output "PUBLISH FAIL stamp build_kind=$($stamp.build_kind) but -Kind $Kind"; exit 1 }
$userVersion = if ($stamp.git_sha) { "$($stamp.version)+$($stamp.git_sha)" } else { "$($stamp.version)" }

Write-Output "== staging $buildDir -> $staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
$excluded = @('*_BackUpThisFolder_ButDontShipItWithYourGame', '*_BurstDebugInformation_DoNotShip')
robocopy $buildDir $staging /E /XD @excluded /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Output "PUBLISH FAIL staging copy robocopy exit=$LASTEXITCODE"; exit 1 }
$leftovers = Get-ChildItem $staging -Directory -Recurse | Where-Object { $name = $_.Name; $excluded | Where-Object { $name -like $_ } }
if ($leftovers) { Write-Output "PUBLISH FAIL do-not-ship folders reached staging: $($leftovers.FullName -join ', ')"; exit 1 }
$sizeMb = (Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum / 1MB

Write-Output "== butler validate platform=$platform arch=amd64"
& $Butler validate --platform $platform --arch amd64 --assume-yes $staging
if ($LASTEXITCODE -ne 0) { Write-Output "PUBLISH FAIL butler validate exit=$LASTEXITCODE staging=$staging"; exit 1 }

$slug = if ($Project) { $Project } else { '<user>/<game>' }
$pushArgs = @('push', $staging, "${slug}:$Channel", '--userversion', $userVersion)
Write-Output ("butler {0}" -f ($pushArgs -join ' '))
if (-not $Push) {
    Write-Output ("PUBLISH DRY-RUN validated target={0} kind={1} channel={2} version={3} size_mb={4:0.0} (pass -Push -Project user/game to upload)" -f $Target, $Kind, $Channel, $userVersion, $sizeMb)
    exit 0
}
if (-not $Project) { Write-Output 'PUBLISH FAIL -Push needs -Project user/game'; exit 1 }
& $Butler @pushArgs
if ($LASTEXITCODE -ne 0) { Write-Output "PUBLISH FAIL butler push exit=$LASTEXITCODE"; exit 1 }
Write-Output ("PUBLISH PASS pushed {0}:{1} version={2}" -f $Project, $Channel, $userVersion)
