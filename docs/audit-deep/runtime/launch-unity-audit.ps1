$ErrorActionPreference = 'Stop'
$auditRepository = 'F:\tmp\synaptic-sea-playable'
$auditOutput = Join-Path $auditRepository 'docs\audit-deep\runtime'
$auditBackup = Join-Path $auditOutput 'prior-generation-captures'
New-Item -ItemType Directory -Force $auditBackup | Out-Null
$captureFolder = Join-Path $auditRepository 'artifacts\screenshots'
Get-ChildItem -LiteralPath $captureFolder -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(purposeful|constrained|expedition)-' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $auditBackup $_.Name) }
$unityAuditArgs = @('-batchmode', '-projectPath', "$auditRepository\SynapticSea", '-runTests', '-testPlatform', 'PlayMode', '-testFilter', 'SynapticSea.Tests.PlayMode.ExpeditionScenePlayModeTests', '-testResults', "$auditOutput\unity-expedition.xml", '-logFile', "$auditOutput\unity-expedition.log")
$unityAuditProcess = Start-Process -FilePath 'F:\Unity\6000.6.0f1\Editor\Unity.exe' -ArgumentList $unityAuditArgs -WindowStyle Hidden -PassThru
[ordered]@{ pid=$unityAuditProcess.Id; started_utc=[DateTime]::UtcNow.ToString('o'); editor='F:\Unity\6000.6.0f1\Editor\Unity.exe'; project="$auditRepository\SynapticSea"; arguments=$unityAuditArgs; scope='existing GPU batch PlayMode fixtures, isolated test state; no native OS input or sustainable acquisition claim'; capture_backup=$auditBackup } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$auditOutput\unity-launch.json" -Encoding utf8
Write-Output "Unity audit PID=$($unityAuditProcess.Id); results=$auditOutput\unity-expedition.xml"
