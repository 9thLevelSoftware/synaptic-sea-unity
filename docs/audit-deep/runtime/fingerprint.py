"""Read-only source fingerprint and experiment provenance writer."""
import datetime, hashlib, json, pathlib, subprocess, sys
ROOT=pathlib.Path(__file__).resolve().parents[3]
OUT=pathlib.Path(__file__).resolve().parent
def git(*args): return subprocess.check_output(['git','-c',f'safe.directory={ROOT.as_posix()}',*args],cwd=ROOT).decode(errors='replace')
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
files=[]
for name in git('ls-files').splitlines():
    path=ROOT/name
    if path.is_file() and (name.startswith('SynapticSea/Assets/') or name.startswith('SynapticSea/ProjectSettings/') or name in ('SynapticSea/Packages/manifest.json','SynapticSea/Packages/packages-lock.json')):
        files.append({'path':name,'sha256':sha(path)})
fingerprint=hashlib.sha256('\n'.join(r['path']+':'+r['sha256'] for r in files).encode()).hexdigest()
dirty=git('status','--porcelain')
artifacts={str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in OUT.glob('bin/Debug/net8.0/AuditRuntime.*') if p.is_file()}
unity_artifacts={str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in (ROOT/'SynapticSea/Library/ScriptAssemblies').glob('SynapticSea.*.dll') if p.is_file()}
manifest={'revision':git('rev-parse','HEAD').strip(),'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'tracked_game_source_config_asset_sha256':fingerprint,'tracked_game_files':files,'dirty_status':dirty,'dirty_status_sha256':hashlib.sha256(dirty.encode()).hexdigest(),'tracked_game_diff':git('diff','--','SynapticSea'),'diagnostic_source_sha256':sha(OUT/'Program.cs'),'compiled_artifacts':artifacts,'model_project':str(OUT/'AuditRuntime.csproj'),'sdk':'F:/Tools/dotnet/dotnet.exe','model_inputs':{'fixture':'provisioned fresh models; real StreamingAssets catalogs','seed':17,'class':'not_applicable_Model_only','storage':'runtime/results-v2/disposable-profile; no user profile'},'command':'dotnet run --project docs/audit-deep/runtime/AuditRuntime.csproj -- F:/tmp/synaptic-sea-playable F:/tmp/synaptic-sea-playable/docs/audit-deep/runtime/results-v2','limits':['Compiled harness is current engine-free Core, not Unity/Windows player.','Retained development build dc2a187 is not used to certify current runtime.','No natural acquisition/sustainability implied by provisioned diagnostics.']}
version=sys.argv[1] if len(sys.argv)>1 else 'v6'
manifest['model_inputs']['storage']=f'runtime/results-{version}/disposable-profile and staged-save-profile; RunSession probes also use MemoryStorage; no user profile'
manifest['command']=f'F:/Tools/dotnet/dotnet.exe run --project docs/audit-deep/runtime/AuditRuntime.csproj -- F:/tmp/synaptic-sea-playable F:/tmp/synaptic-sea-playable/docs/audit-deep/runtime/results-{version}'
manifest['model_result_sha256']=sha(OUT/f'results-{version}/model-results.json')
manifest['unity_artifacts']=unity_artifacts
manifest['unity']={'result_sha256':sha(OUT/'unity-expedition.xml'),'log_sha256':sha(OUT/'unity-expedition.log'),'fixture':'ExpeditionScenePlayModeTests; six existing fixtures, seed17/42/777, condition0; all portals opened for geometry subset','inputs':'batchmode GPU PlayMode; no native OS input, no user profile','transient_editor_changes':'Unity import added app-ui EditorBuildSettings config and replaced Standalone define SENTIS_ANALYTICS_ENABLED with APP_UI_EDITOR_ONLY; compiled tests with these generated settings; restored only those two tracked differences after process exit','limits':'No full HUD in these fixture captures; no earned acquisition, natural journey or development-executable certification'}
manifest['unity_edit']={'result_sha256':sha(OUT/'unity-targeted-edit.xml'),'log_sha256':sha(OUT/'unity-targeted-edit.log'),'result':'28 passed, zero failed/skipped','scope':'existing HomeAssemblyGeometry, InteriorOcclusion, AudioContent, HudLayout, StructuralPrefabCollision fixtures; no Title transition or native input'}
captures=['constrained-overview-17.png','constrained-medical-17.png','constrained-engineering-42.png','purposeful-cargo-777.png']
manifest['visually_reviewed_current_captures']={name:sha(ROOT/'artifacts/screenshots'/name) for name in captures}
(OUT/f'experiment-manifest-{version}.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'revision':manifest['revision'],'game_fingerprint':fingerprint,'tracked_game_diff_empty':not manifest['tracked_game_diff'],'compiled_artifacts':artifacts}))
