$ErrorActionPreference='Stop'
$auditRepo='F:\tmp\synaptic-sea-playable'
$auditOut=Join-Path $auditRepo 'docs\audit-deep\runtime'
$auditFilters='SynapticSea.Tests.Unity.HomeAssemblyGeometryTests;SynapticSea.Tests.Unity.InteriorOcclusionTests;SynapticSea.Tests.Unity.AudioContentTests;SynapticSea.Tests.Unity.HudLayoutTests;SynapticSea.Tests.Unity.StructuralPrefabCollisionTests'
$auditArgs=@('-batchmode','-projectPath',"$auditRepo\SynapticSea",'-runTests','-testPlatform','EditMode','-testFilter',$auditFilters,'-testResults',"$auditOut\unity-targeted-edit.xml",'-logFile',"$auditOut\unity-targeted-edit.log")
$auditProc=Start-Process -FilePath 'F:\Unity\6000.6.0f1\Editor\Unity.exe' -ArgumentList $auditArgs -WindowStyle Hidden -PassThru
[ordered]@{pid=$auditProc.Id;started_utc=[DateTime]::UtcNow.ToString('o');arguments=$auditArgs;scope='existing isolated EditMode fixtures; no new gameplay code or native input'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$auditOut\unity-edit-launch.json"
Write-Output "Targeted Edit audit PID=$($auditProc.Id)"
