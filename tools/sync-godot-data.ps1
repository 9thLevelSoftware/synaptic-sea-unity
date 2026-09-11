<#
.SYNOPSIS
  Mirrors the Godot project's runtime JSON data into the Unity project's StreamingAssets.

.DESCRIPTION
  Copies data/**/*.json from the Godot repo into SynapticSea/Assets/StreamingAssets/data, keeping the exact
  relative paths so res:// strings inside the data resolve unchanged. Offline pipelines (training,
  asset_generation, comfyui) and Godot-only files (.tres, .import, .uid, .wav) are excluded; audio clips are
  imported separately as Unity assets. Stale files in the destination are removed. The source commit is
  recorded in docs/data-sync.json.

.EXAMPLE
  pwsh tools/sync-godot-data.ps1
  pwsh tools/sync-godot-data.ps1 -Source F:\work\godot-parity-fixtures
#>
param(
    [string]$Source = 'D:\the-synaptic-sea',
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$srcData = Join-Path $Source 'data'
$dstData = Join-Path $RepoRoot 'SynapticSea\Assets\StreamingAssets\data'
$excludedDirs = @('training', 'asset_generation', 'comfyui')

if (-not (Test-Path $srcData)) { throw "Godot data folder not found: $srcData" }

$files = Get-ChildItem -Path $srcData -Recurse -File -Filter *.json | Where-Object {
    $rel = $_.FullName.Substring($srcData.Length + 1)
    $top = $rel.Split([IO.Path]::DirectorySeparatorChar)[0]
    -not ($excludedDirs -contains $top)
}

New-Item -ItemType Directory -Force $dstData | Out-Null
$expected = @{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($srcData.Length + 1)
    $dst = Join-Path $dstData $rel
    $expected[$dst.ToLowerInvariant()] = $true
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
}

# Remove files that no longer exist upstream (keep Unity .meta files for surviving assets).
$removed = 0
Get-ChildItem -Path $dstData -Recurse -File | Where-Object { $_.Extension -ne '.meta' } | ForEach-Object {
    if (-not $expected.ContainsKey($_.FullName.ToLowerInvariant())) {
        Remove-Item -LiteralPath $_.FullName -Force
        $meta = "$($_.FullName).meta"
        if (Test-Path $meta) { Remove-Item -LiteralPath $meta -Force }
        $removed++
    }
}

$sha = (git -C $Source rev-parse HEAD).Trim()
$dirty = [bool](git -C $Source status --porcelain -- data)
$record = [ordered]@{
    source_repo      = $Source
    source_sha       = $sha
    source_data_dirty = $dirty
    synced_utc       = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    file_count       = $files.Count
    excluded_dirs    = $excludedDirs
}
$record | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $RepoRoot 'docs\data-sync.json')

$bytes = ($files | Measure-Object Length -Sum).Sum
Write-Output ("DATA SYNC PASS files={0} bytes={1} removed={2} source_sha={3} dirty={4}" -f $files.Count, $bytes, $removed, $sha.Substring(0, 8), $dirty)
