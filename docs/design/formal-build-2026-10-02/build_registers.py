"""Build design registers from the frozen deep audit; documentation only.

Outputs are drafts, never product acceptance results. Existing audit/game bytes
are read only. Preservation baseline is captured once and never overwritten.
"""
import csv
import datetime
import hashlib
import json
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[3]
OUT = pathlib.Path(__file__).resolve().parent
AUDIT = ROOT / 'docs/audit-deep'
PREFIX = 'SynapticSea/Assets/_Project/'

def read(path):
    return json.loads((ROOT / path).read_text(encoding='utf-8-sig'))

def save(name, value):
    (OUT / name).write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def git(*args):
    return subprocess.check_output(['git', '-c', 'safe.directory=' + ROOT.as_posix(), *args], cwd=ROOT).decode().strip()

observed_head = git('rev-parse', 'HEAD')
head = '840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48'

if not (OUT / 'preservation-baseline.json').exists():
    raise RuntimeError('Original authoring preservation baseline missing; never reconstruct it from a concurrently edited tree')

m = read('docs/audit-deep/runtime/experiment-manifest-v7.json')
save('provenance.json', {
    'schema': 'synaptic-design-provenance-1', 'status': 'draft_for_user_review',
    'revision': head, 'observed_current_head': observed_head, 'date': '2026-10-02',
    'audit_baseline':head,
    'approved_implementation_baseline':'15b09d778763c5725c16d56df3244a0ab317d0c0',
    'prior_bounded_implementation_baseline':'a52a347b30c406e5beedd120f0ed7d46b49a5e97',
    'approved_product_commits':['c68c3fe80c322c213b076ffdd1f588596097d1cf','e062f00386c4ce789dc2a481a5a33568902ff85a','52027d3e8cf31a699c4a4f2b94ea0b7cb571462c'],
    'bounded_engineering_evidence':{'path':'docs/implementation/foundation-bounded-2026-10-02.md','verification':'docs/implementation/foundation-bounded-2026-10-02-verification.json','core_passed':919,'edit_passed':1204,'edit_vendor_environment_skipped':17,'selected_play_passed':14,'attribution':'Independently approved parent engineering, not tests run by this documentation task; no native/campaign/all-class/multifile acceptance'},
    'station_tier_evidence':{'commit':'15b09d778763c5725c16d56df3244a0ab317d0c0','path':'docs/implementation/station-tier-2026-10-02.md','verification':'docs/implementation/station-tier-2026-10-02-verification.json','core_passed':937,'edit_passed':1229,'edit_vendor_environment_skipped':17,'selected_play_passed':14,'anchor_probe_2':'one empty-container diagnostic passed; production descriptor/root/occupancy and earned/native walking remain unqualified','attribution':'Parent engineering evidence, not executions by this documentation task; narrower than full F07'},
    'concurrent_engineering': {'thread_id':'01a0fb2d-7661-723d-be69-35572c2ab0f9','authorized_scope':'Parent admitted narrow F01 validator, current F04 locality/hold, existing F05 replacement/temp recovery. Documentation task does not mutate/restore product changes or rebaseline them.'},
    'published_main_as_audited': '8dcc95c10ab5e08658546319f51f49b4abd256fc',
    'published_main_check': 'carried from deep audit; no fresh remote query in documentation stage',
    'authoring_ahead_count_as_audited': 27,
    'observed_ahead_count_verified': int(git('rev-list', '--count', '8dcc95c..HEAD')),
    'origin_verified': git('remote', 'get-url', 'origin'),
    'companion_local_verified': '00735856e4d06e5823608e0ab77832a992baf098',
    'embedded_companion': '01230d84c895c13fdb744df98d5b39724c35ba44',
    'audit_v7_source_fingerprint': m['tracked_game_source_config_asset_sha256'],
    'audit_v7_result_hash': m['model_result_sha256'],
    'evidence_counts': {'generation_model_probes': 643, 'core_provisioned_diagnostics': 21, 'gpu_play_fixtures': 6, 'targeted_edit_fixtures': 28, 'inventoried_files': 2911, 'inventoried_nonmeta': 1542, 'exact_catalog_symbol_files': 61, 'nonmeta_without_reconciled_exact_symbols': 1481},
    'library_reference': {'library_file_id': 'libfile_2ab6994b66b88191a01fa7250b603dbe', 'file_id': 'file_00000000b2e081f5a13a5eaaca963f4c', 'file_name': 'synaptic-sea-report-companion.zip', 'version': 0, 'expected_bytes': 78623, 'status': 'not_materialized', 'limit': 'Current Library transfer helper fails on Windows: os.setxattr unavailable; required metadata not applied; no alternate transfer used'},
    'repo_instructions': 'No applicable AGENTS.md/.agents found in checkout/ancestor locations; docs/design, TickOrder and InteractionOrder conventions retained.',
    'write_scope': 'New design/planning documents only; no product/balance/save/asset/audit/ref mutation'})

# Tests are specification records, not executable tests or pass claims.
test_rows = [
 ('A01','CatalogAndSourceContracts','CatalogSourceValidatorTests','EditMode/Systems/Infra','catalog_static','Remove a tool/form/book/repair input producer; include unknown IDs, duplicate mass and diagnostic-only grant edges','Exact errors include row/caller/source; all production required references resolve; deferred exposure explicit'),
 ('A02','FiniteSeededAcquisitionRoutes','AcquisitionSourceTests','EditMode/Systems/Infra','finite_model_diagnostic','All62 AND recipes,17 missing input IDs,31 sourceless item rows,seedless coolant cycle and finite class scenarios','Required chains have external seed/live source and sufficient finite quantities; no cycle or grant assumed earned'),
 ('A03','DamagedComponentHolderRoundTrip','ComponentTransferTests','EditMode/Systems/Inventory','package_model_unit_then_F06_saved_integration','Actual reactor_console0.23 vs0.81; A->bag->cargo->B->A; equal local slots; F06 separately adds Continue','Same instance/condition/mass and one holder; reviewed operability; no0.55 heal; unit exit does not require F06'),
 ('A04','OverflowAndRemainderConservation','LootRemainderTests','EditMode/Session','package_model_unit_then_F06_saved_integration','Full/partial destination,component failure,serialize/apply remainder; F06 later actual Save/Continue','Accepted units only debited; remainder conserves; failed component stays original holder; no dependency on future class journey'),
 ('A05','WorkLocalityHoldAndContext','WorkLocalityTests','EditMode/Session','model_and_normal_acquisition','Held generic cut moves1414m; release; toggle; LOS loss; equal IDs on another ship; tool loss','Begin/advance/commit valid; release no progress/stamina/effects; invalid site interrupts; owner isolation'),
 ('A06','WorkResumeAndCommitOnce','WorkRestoreTests','EditMode/Session','model_and_normal_acquisition','Save held/toggle job; released input Continue; damage/exhaustion; duplicate completion','Paused restore and explicit resume; exact denial; no output/XP/material replay; actual receipt persisted'),
 ('A07','FilesystemCommitFaultRecovery','SaveCommitRecoveryTests','EditMode/Systems/Save','disposable_real_disk_diagnostic','Locks/disk write failure at every payload/manifest/pointer/index boundary with restart','Old or new validated complete generation; no mixed state or lost only good save; visible error'),
 ('A08','TempAndIndexRecovery','SaveCommitRecoveryTests','EditMode/Systems/Save','disposable_real_disk_diagnostic','Valid staged legacy temp absent destination; corrupt pointer,index/mismatched world-run','Complete consistent recovery or explicit ambiguity; index reconstructs from manifests; bytes retained'),
 ('A09','LegacyVersionMigration','LegacyInstanceMigrationTests','EditMode/Systems/Save','model_and_disposable_disk_diagnostic','All old run/world families; anonymous forms; duplicated owners; malformed/future versions; interruption','Pure idempotent clone; original bytes and known condition retained; no free engine; invalid live apply unchanged'),
 ('A10','EveryClassSoloBootstrap','ClassBootstrapPlayModeTests','PlayMode','normal_earned_journey','Eight fresh Title classes plus three legitimately unlocked; no hub perk; ordinary objective orders; finite six hull_sealant','Each earns shelter and repaired powered controllable first craft departure/return; distinct tools/skills/costs; input witness'),
 ('A11','TierKnowledgeAndQueueParity','RecipeGateParityTests','EditMode/Session','package_model_then_F09_normal_learning','Tier1 sensor/tier2 nozzle,preview/start/advance/complete phases; exact inputs consumed once; paid receipt,outage/Continue','Start requires actual tier/knowledge/materials; paid completion needs no second set; output once; start quality persists; unsafe queue rejects'),
 ('A12','OfferedLaunchContract','FrontEndPlayModeTests','PlayMode','instantiated_real_scene_then_native','Offer current seed17/breach_field/standard vs unsupported selectors; actual SceneLoader','Every enabled choice actually launches; actual run difficulty labeled; unsupported choices fail closed without misleading UI'),
 ('A13','VisibleCaptionsCurrentWarnings','HudLayoutTests','EditModeUnity','instantiated_ui_then_native','Registered threat/fire SFX with captions,simultaneous alerts,2x scale; recover oxygen100; inert control audit','Visible actual label text/duration; danger clears while history remains; controls accurately affect state or are disabled'),
 ('A14','SecondaryHabitatServices','VesselServicePlayModeTests','PlayMode','normal_earned_journey','Earn claimed repaired/equipped secondary hull; craft/produce,local power outage,save inside/revisit','Services belong to that vessel; no free stations/safety/crops; original home unaffected; one harvest/local collection'),
 ('A15','InternalExteriorCutReseal','BoundaryTopologyPlayModeTests','PlayMode','normal_work_instantiated_geometry','v4 seed17 dock/cargo internal and crew/outside solid edge timed cuts,revisit/reseal','Internal passage distinct from outside breach; actual collider/NavMesh/LOS/fire state reconcile; stable boundary'),
 ('A16','ConnectionServiceIsolation','ServiceConnectionTests','EditMode/Systems/Travel','model_then_instantiated','Mechanical join closed door,no conduit,then deliberate service links/outage/save','Independent graphs and local power; chosen atmosphere conservation; closed air barrier; no automatic share'),
 ('A17','EarnedAssemblyMobilityAndScout','AssemblyMobilityPlayModeTests','PlayMode','normal_earned_journey','Earn engines/parts; A-B-C secured assembly,stowed craft,insufficient load,move/save/Continue,small craft outing','Mass/eligible thrust once; no stowed thrust; stable relative poses/doors; scout detach/return utility; no victory'),
 ('A18','OccupiedNestedDetachRollback','ConnectionDetachPlayModeTests','PlayMode','instantiated_then_normal','Occupied detach,invalid landing/site/overlap/cycle,A-B-C detach/rejoin,save','Reject preserves graph/geometry/resources; valid subtree moves once and keeps exact endpoints/player-safe location'),
 ('A19','ActiveWorkingSetScaling','ShipRegistryScaleTests','EditMode/Systems/Travel','model_plus_loaded_performance','0/10/100/1000 visits same active set; snapshot dirty revisions/root residency','Visited state conserved; per-frame active scan independent of history; measured CPU/GPU/memory/save separate; budget after OPEN11'),
 ('A20','AbsentTimePolicyAndCursor','ShipSimulationPolicyTests','EditMode/Systems/Travel','model_then_session','300/1800/3600/86400 seconds single/segmented/active; same timestamp; save pending catchup','Chosen policy tolerance explicit; no silently stamped unprocessed time; no double tick; each subsystem cursor conserved; no offline claim'),
 ('A21','RepeatedExpeditionRecovery','LongHaulSurvivalPlayModeTests','PlayMode','normal_earned_then_native_campaign','Three varied expeditions:care/supplies,reclaim/equip,new contact+depleted revisit; injury/outage/load/Continue','Actual resource ledger and recovery,finite depletion/resupply,scout return; campaign duration/balance chosen separately'),
 ('A22','TopologyPerceptionInvalidation','ThreatNavMeshPlayModeTests','PlayMode','instantiated_then_normal','Door/internal cut/reseal enemy sensing,path,acoustics; occluded rendering','AI physics perception follows topology; render occlusion does not reveal through solid world; edit caches refresh'),
 ('A23','BoundedColonyState','BiomassColonyTests','EditMode/Systems/Combat','conditional_model_then_normal','After OPEN10 choose finite colony budget,resource nodes,frontier and response; kill/revisit/load','Resource-funded bounded spawn/growth; persisted territory/response; no free respawn/ML assumption'),
 ('A24','GeneratedOpportunityPhysicalCoverage','ExpeditionScenePlayModeTests','PlayMode','catalog_model_and_instantiated','Current profiles/fallbacks/selected seeds; furnished/damaged approaches,multideck scope,live camera after edits','Exact program_id/version; protected reachable producers and apertures; no invalid actual_seed metric; visual/HUD measured'),
 ('A25','ApprovedLibraryNormalEncounter','ThreatCreatureFactoryTests','EditModeUnity','normal_encounter_plus_instantiated','Selected approved library actually registered; organic normal encounter/save/restore; unsupported/missing pool','Manifest matches package/build/decisions; actual view/controller; exact persisted identity,explicit fallback,no reroll'),
 ('A26','CompanionDecisionFreshness','CompanionDecisionFreshnessTests','companion_tests','isolated_companion_diagnostic','Disposable copied approved/rejected skeleton/part transitions then pack without rebuild; stale receipt','Decision mismatch refuses or reconciles per reviewed release policy; unchanged approved crawler not reapproved; geometry reusable'),
 ('A27','SceneAudioSettingsLifecycle','AudioLifecyclePlayModeTests','PlayMode','real_scene_then_native_audio','First Title->run->Title->new run/Continue; listeners,model binding/voices; settings NaN/infinity','One active listener; old session unbound/stopped; native audio verified separately; finite settings compatibility'),
 ('A28','CargoAgeAndContamination','CargoAgePlayModeTests','PlayMode','conditional_normal_earned','After OPEN09 choose storage ageing; old/new food batches,cargo travel/load,long absence','No fresh reset of old stock; storage owner/timing conserved; chosen contamination/spoilage costs evidenced'),
 ('A29','NativeFoundationAccessibility','NativeJourneyEvidence','docs/playtest','native_manual','Actual Windows player keyboard/mouse/gamepad,Title/first ship/save/revisit,1200x800/2x caption and panel checks','Exact build/source/input recorded; all required controls reachable/readable; no batch fixture masquerades as native'),
 ('A30','TravelCostRecoveryLogistics','TravelResourceTransactionTests','EditMode/Systems/Travel','conditional_model_then_normal','After OPEN09/12 choose travel costs/tow/load; actual travel debit,rollback,scout vs home','Actual accepted resource debit once; failed travel preserves resources/poses; helpers alone not treated as live charging'),
 ('A31','InstanceSelectionAndDestinationDiscovery','ComponentInstancePanelTests','EditModeUnity','package_UI_commands_then_F09_normal_input','InventoryPanel/ShipModificationPanel mouse/keyboard/gamepad choose same-form0.23 vs0.81; reorder; actual incompatible and compatible slots','Stable instance command picks selected object; no panel mutation; physical install prompt; explicit compatibility/condition/tool/range reason'),
 ('A32','AtomicDomainBoundaryFaults','DomainTransactionTests','EditMode/Session','package_model_unit_then_F06_saved_receipt','Inject before/after registry,placement,inventory,machinery,job/payment,XP/training log,receipt,publication and notification','Sole coordinator publishes all-or-none; precommit old state hash/no effects; postpublish full new bundle; retry no duplication'),
 ('A33','ExactlyOnePaidCraftThroughOutageContinue','PaidCraftStateTests','EditMode/Session','package_phase_unit_and_F06_saved_integration','One exact ingredient set; start consumes to zero; power outage,serialize/Continue,pending output full,duplicate completion','Paid receipt/quality persists; no second material requirement/debit/refund; exactly one accepted output and one receipt'),
 ('A34','SlotPayloadBindingAndFrozenRecovery','SlotPayloadBindingTests','EditMode/Session','package_plain_schema_then_F06_integrated_restore','world/checkpoint,active/rotating,quick/manual full bundle; index corrupt after FreezeRun; predeath parent/temp available','Matching complete run/world/layout revision; manual restore whole captured world; no ancestor resurrection/tombstone clearing; legacy limit explicit'),
 ('A35','LegacyCraftPaymentReconciliation','LegacyCraftReconciliationTests','EditMode/Session','legacy_diagnostic_then_actual_UI','Paid BeginCraft and unpaid queue auto-start same active shape; complete status; Keep paused/Abandon/Start fresh and reload','Both unproved records stay paused_unverified; no automatic output/refund; explicit fresh payment/full time; original/archive/disposition retained once'),
 ('A36','ReachableLegacyUnknownOverhaul','LegacyEquipmentReconciliationTests','EditMode/Session','migration_preflight_and_normal_input','Unknown null cargo; actual acquired wrench/raw1+1/passive surface/30 nominal seconds; essential unreachable or budget shortage; save interruption','Newly achieved0.60 paid provenance/zeroXP, same instance and unchanged machinery; no previous-health guess; unresolved essential conversion leaves original bytes/live world intact'),
 ('A37','RealUtilityRepairAndFiniteRecovery','HomeUtilityRepairTests','EditMode/Session','model_consumers_then_normal_acquisition','Four named feed/relay/light/beacon defects; raw quantities;12 nominal seconds/main stamina+wounds/rest; ordinary primary4+4 spending then racks8+8; objective orders','Actual consumers/views change; direct60XP/class fraction and record-only receipt once; no part-category shortcut/RepairPoint25 duplication/farming/refill; bounded recovery includes four jobs'),
 ('A38','RetainedManualStudy','ManualStudyTests','EditMode/Session','model_then_actual_UI_and_Continue','Carried book30 eligible stationary seconds; release/copy loss/injury/Continue; duplicate copy; legacy BooksRead; real Study/View UI','Manual retained; no industrial stamina drain; atomic BooksRead+knowledge+authoredXP once per character/book; legacy read seeds knowledge with no XP replay'),
 ('A39','EarlyWorkAndAppliedTrainingPrimitives','WorkKernelTests','EditMode/Systems/WorkActions','early_kernel_unit_no_gameplay_acceptance','Owner/range/hold/effort/progress/paused serialization with fake commit port; record-only event and restored progression replay','Kernel has no concrete F03/F04/F08 dependency; no live mutation without bound port; RecordApplied grants zero XP and receipt-owned replay never double-awards')]

def unity_target(cls,folder,filename,expected_cases,package):
    mode='EditMode' if folder=='EditModeUnity' else 'PlayMode'
    fullname=('SynapticSea.Tests.Unity.' if folder=='EditModeUnity' else 'SynapticSea.Tests.PlayMode.')+cls
    return {'package':package,'target_path':PREFIX+'Tests/'+folder+'/'+cls+'.cs','target_status':'proposed','test_class_or_record':cls,'evidence_scope':'Instantiated real UI/consumer/view with explicitly provisioned fixture state; earned acquisition remains F09','runner':{'project':'SynapticSea','assembly':'SynapticSea.Tests.'+folder,'mode':mode,'proposed_fixture_fullname':fullname,'expected_case_names':expected_cases,'execute':'& "$env:LOCALAPPDATA\\Unity\\bin\\unity.exe" test "F:\\tmp\\synaptic-sea-playable\\SynapticSea" --mode '+mode+' --filter "'+fullname+'" --output "F:\\tmp\\synaptic-sea-playable\\artifacts\\foundation-c6\\run-01\\'+filename+'.xml"','xml_requirement':'Foundation plan Assert-UnityFixtureXml: every expected case fullname present, target count > 0, every target result Passed; missing/zero/skipped-only is failure. Separate output each attempt.'}}
tests=[]
for tid,title,cls,folder,scope,scenario,expected in test_rows:
    if folder=='companion_tests':
        target='D:/critter-creator/tests/test_library_decision_freshness.py'
    elif folder=='docs/playtest':
        target='docs/playtest/formal-foundation-native/evidence.json'
    else:
        target=PREFIX+'Tests/'+folder+'/'+cls+'.cs'
    if folder.startswith('EditMode/'):
        runner={'project':'tools/dotnet/SynapticSea.Core.Tests/SynapticSea.Core.Tests.csproj','discovery':'dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter FullyQualifiedName~'+cls,'execute':'dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter FullyQualifiedName~'+cls,'requirement':'Assert expected test names and nonzero executed count; zero-filter match is not PASS'}
    elif folder in ['EditModeUnity','PlayMode']:
        mode='EditMode' if folder=='EditModeUnity' else 'PlayMode'
        fullname=('SynapticSea.Tests.Unity.' if folder=='EditModeUnity' else 'SynapticSea.Tests.PlayMode.')+cls
        runner={'project':'SynapticSea','assembly':'SynapticSea.Tests.'+folder,'mode':mode,'proposed_fixture_fullname':fullname,'execute':'& "$env:LOCALAPPDATA\\Unity\\bin\\unity.exe" test "F:\\tmp\\synaptic-sea-playable\\SynapticSea" --mode '+mode+' --filter "'+fullname+'" --output "F:\\tmp\\synaptic-sea-playable\\artifacts\\foundation-v3-review\\run-01\\'+cls+'.xml"','requirement':'Verify actual/proposed fixture namespace before run; assert intended fullnames/nonzero execution from XML. Core cannot discover this assembly; use new output per attempt'}
    else:
        runner={'project':'isolated companion fixture' if folder=='companion_tests' else 'native manual evidence','execute':'Scoped runner or native input record after review','requirement':'No dotnet/Unity discovery claim for this target'}
    test={'id':tid,'title':title,'status':'planned_not_run','test_class_or_record':cls,'target_path':target,'target_status':'existing_anchor' if (ROOT/target).is_file() else 'proposed','evidence_scope':scope,'scenario':scenario,'expected':expected,'provisioning_policy':'All diagnostics labeled; normal/native gates require finite earned acquisition','runner':runner}
    if tid=='A02':
        test['additional_target_paths']=[PREFIX+'Tests/EditMode/Session/ClassBootstrapRouteTests.cs']
        test['additional_discovery']='dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter FullyQualifiedName~ClassBootstrapRouteTests'
        test['additional_execute']='dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter FullyQualifiedName~ClassBootstrapRouteTests --results-directory artifacts/foundation-v3-review/run-01'
    if tid in ['A35','A36','A37','A38']:
        test['normal_input_gate']='F09 actual UI/acquisition/Continue; package provisioned tests cannot substitute'
    if tid=='A37':
        test['additional_targets']=[unity_target('AuxiliaryUtilityViewPlayModeTests','PlayMode','utility-views',['FabricatorFeedProjectsCommittedPower','CargoRelayEnablesBulkCommandsPreservesManualAccess','MedbayLampProjectsUtilityWithoutHealing','DockBeaconShowsActualDockAndAirlockState'],'F08')]
        test['additional_target_paths']=[x['target_path'] for x in test['additional_targets']]
    if tid=='A38':
        test['additional_targets']=[unity_target('ManualStudyPanelTests','EditModeUnity','manual-study-ui',['CarriedManualOpensTimedStudyAndView','InterruptedStudyAndContinueRemainPaused','RepeatedCopiesAndLegacyReadDoNotAwardAgain'],'F07')]
        test['additional_target_paths']=[x['target_path'] for x in test['additional_targets']]
    if tid=='A39':
        test['additional_target_paths']=[PREFIX+'Tests/EditMode/Systems/Progression/TrainingReceiptTests.cs']
        test['additional_discovery']='dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter FullyQualifiedName~TrainingReceiptTests'
        test['additional_execute']='dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter FullyQualifiedName~TrainingReceiptTests --logger "trx;LogFileName=training-receipts.trx" --results-directory artifacts/foundation-c6/run-01'
    tests.append(test)
save('acceptance-tests.json',{'schema':'synaptic-acceptance-plan-1','revision':head,'tests':tests})

def ref(relative,symbol):
    return {'file':PREFIX+relative,'symbol':symbol}

def system(relative,symbol):
    return ref('Core/Systems/'+relative,symbol)

def session(relative,symbol):
    return ref('Core/Session/'+relative,symbol)

work_rows=[
 ('F01','Catalog/source validation','foundation',[],['A01','A02'],[system('Inventory/ItemDefs.cs','LoadDefinitions'),system('Infra/DependencyValidator.cs','VerifyCatalogSources'),system('Infra/CatalogSourceValidator.cs','LoadProductionCatalog'),system('Infra/CatalogSourceValidator.cs','NormalizeSources'),session('RunSession.Build.cs','GetCatalogSourceRegistrations'),session('RunSession.Build.cs','GetCatalogValidationReport')],[],'Bounded on-demand diagnostics delivered; finite sources/physical exposure remain pending'),
 ('F02','Reachable source/tool/form amendment','foundation',['F01'],['A01','A02','A11'],[session('RunSession.WorkAction.cs','TryWorkActionInteract'),ref('Core/Procgen/GameplaySliceBuilder.cs','Build')],[],'F02-A selected provisional supplies/recovery; descriptor/physical qualification and measured survival/routes before acceptance'),
 ('F04A','Shared work and applied-receipt primitives','foundation',['F01'],['A39'],[system('WorkActions/WorkActionDriver.cs','Tick'),system('Progression/TrainingEventBus.cs','ReplayInto'),system('Progression/PlayerProgressionState.cs','GrantXp')],['Core/Systems/WorkActions/IWorkCommitPort.cs','Core/Systems/WorkActions/WorkEligibility.cs','Core/Systems/WorkActions/WorkTransactionState.cs','Tests/EditMode/Systems/WorkActions/WorkKernelTests.cs','Tests/EditMode/Systems/Progression/TrainingReceiptTests.cs'],'Early engine-free kernel/port and RecordApplied; no concrete domain/session/utility dependency or live publication'),
 ('F03','Instance/loot conservation','foundation',['F02','F04A'],['A03','A04','A31','A32','A36'],[system('Inventory/InventoryState.cs','AddItem'),system('Inventory/CargoTransfer.cs','MoveItem'),system('ShipSystems/ComponentPlacementState.cs','Mount'),system('ShipSystems/ComponentMountResolver.cs','ResolveDismount'),session('Interactables/LootContainer.cs','TryInteract'),ref('UI/Panels/InventoryPanel.cs','OpenTransfer'),ref('UI/Panels/ShipModificationPanel.cs','InstallFromInventory'),ref('Game/SessionUiBridge.cs','InstallRequested')],['Core/Systems/Inventory/ItemInstanceState.cs','Core/Systems/Inventory/ComponentTransferService.cs','Core/Systems/Inventory/LegacyEquipmentReconciliationService.cs','Core/Session/Interactables/LegacyOverhaulPoint.cs','Core/Session/DomainTransactionCoordinator.cs'],'Consume early F04A via port; F03-A and paid overhaul selected provisionally; preserve originals on refused F06 conversion'),
 ('F04','Work session integration and commits','foundation',['F01','F03','F04A'],['A05','A06','A32'],[session('RunSession.WorkAction.cs','TickWorkAction'),system('WorkActions/WorkActionDriver.cs','Tick'),session('Interactables/RepairPoint.cs','Complete')],[],'Retain downstream F03 dependency; integrate early F04A kernel with sole atomic owner, existing strict adapters and HUD'),
 ('F05','Durable save recovery','foundation',['F01','F03'],['A07','A08','A34'],[ref('Core/Services/Storage.cs','FileSystemStorage'),system('Save/SaveLoadService.cs','RecoverLegacyTemporary'),system('Save/SaveIndexState.cs','SaveIndexState'),system('Save/PermadeathResolver.cs','HasDiedIn'),ref('UI/Presenters/SaveSlotScreenModel.cs','SnapshotBuilder')],['Core/Systems/Save/SaveCommitCoordinator.cs','Core/Session/SavePayloadAssembler.cs'],'Pure storage delivered narrowly; full assembly depends F03 committed bundle/revision; generations pending'),
 ('F06','Migration and paused restore','foundation',['F03','F04','F05'],['A06','A09','A32','A33','A34','A35','A36'],[system('Save/SaveMigrationService.cs','MigrateWorld'),session('RunSession.Save.cs','ApplyRunSnapshotInternal'),session('RunSnapshotAssembler.cs','Build'),session('WorldSnapshotAssembler.cs','WorldSnapshotAssembler')],[],'Run7/world5 review; shipped unknown action/preflight and legacy unverified-payment UI required before conversion'),
 ('F07','Recipe tier/knowledge parity','foundation',['F02','F06','F04A'],['A11','A33','A35','A38'],[session('Interactables/CraftingStation.cs','TryCraftRecipe'),session('RunSession.Crafting.cs','ListStationRecipeEntries'),system('Crafting/RecipeKnowledgeState.cs','LearnFromBook'),system('Crafting/CraftingState.cs','BeginCraft'),system('Crafting/CraftingState.cs','EnqueueCraft'),system('Progression/PlayerProgressionState.cs','BooksRead')],['Core/Systems/Crafting/RecipeGateService.cs','Core/Systems/Progression/ManualStudyService.cs'],'Consume F04A RecordApplied/work before study, never F08; selected retained timed study and legacy policy remain measurable hypotheses'),
 ('F08','All-class finite solo routes','foundation',['F07'],['A02','A10','A37'],[session('RunSession.Build.cs','ConfigurePlayerProgression'),session('RunSession.Objectives.cs','OnInteractableCompleted'),session('Interactables/BreachSealPoint.cs','Complete'),session('Interactables/RepairPoint.cs','PrecheckReason'),system('Progression/PlayerProgressionState.cs','GrantXp'),system('Progression/TrainingEventBus.cs','ReplayInto')],['Core/Session/BootstrapRouteValidator.cs','Core/Systems/ShipSystems/ShipAuxiliaryUtilityState.cs','Core/Session/Interactables/AuxiliaryServicePoint.cs','Runtime/Session/Views/AuxiliaryUtilityView.cs'],'OPEN-06 named genuine hardware/raw adapter/eligible timing/finite recovery values; no XP fixtures or universal lethal-choice guarantee'),
 ('F09','Earned foundation acceptance','foundation',['F08','F06'],['A03','A04','A05','A06','A10','A11','A29','A31','A33','A35','A36','A37','A38'],[ref('Tests/PlayMode/PlayableScenePlayModeTests.cs','PlayableScenePlayModeTests')],['Tests/PlayMode/FoundationAcquisitionPlayModeTests.cs','Tests/PlayMode/ClassBootstrapPlayModeTests.cs'],'Normal no-grant paths, actual reconciliation/study/utility consumers and native evidence distinct'),
 ('P01','Truthful launch/caption/tutorial controls','presentation',['F07'],['A12','A13'],[ref('App/Title/TitleScreen.cs','TitleScreen'),ref('Game/SessionUiBridge.cs','SessionUiBridge'),ref('UI/Hud/ObjectiveChip.cs','Refresh'),system('Infra/TutorialState.cs','HasPendingBanner')],['UI/Hud/CaptionChannel.cs'],'Actual displayed/scene behavior'),
 ('P02','Scene/audio/native lifecycle','presentation',['P01','F06'],['A27','A29'],[ref('Runtime/Audio/AudioManager.cs','AudioManager'),ref('App/Boot/AppServices.cs','AppServices'),ref('Runtime/Session/RunSessionHost.cs','Boot')],[],'OPEN-11 before hardware certification; native audio witness'),
 ('H01','Per-vessel services','habitat',['F06','F07','F09'],['A14'],[session('RunSession.Crafting.cs','BuildProductionStations'),session('RunSession.HomeExtension.cs','ActivateBoardedContext'),system('Travel/ShipInstance.cs','GetSummary')],['Core/Systems/Travel/VesselServiceState.cs','Core/Session/VesselServiceContext.cs'],'Earned secondary equipment/local power; migration owner review'),
 ('H02','Typed boundaries and reconciliation','topology',['F04','F06'],['A15','A22','A24'],[system('ShipSystems/ModuleIntegrityMap.cs','ModuleIntegrityMap'),session('RunSession.Fire.cs','BuildFireContext')],['Core/Systems/ShipSystems/BoundaryTopologyState.cs'],'Internal/outside physical edit discriminator'),
 ('H03','Explicit service networks','habitat',['H01','H02'],['A16'],[system('Travel/HomeJoinPlanner.cs','Validate'),session('RunSession.HomeExtension.cs','CompleteHomeWork')],['Core/Systems/Travel/ServiceConnectionState.cs'],'OPEN-02 atmosphere model before service transfer'),
 ('M01','Earned assemblies and reversible scouts','mobility',['H01','H02','F09'],['A17','A18'],[system('Travel/AssemblyMobility.cs','Evaluate'),system('Travel/DockingManager.cs','DockingManager'),session('RunSession.Travel.cs','TravelHome')],[],'Earned install/departure/occupied detach and exact pose restore'),
 ('S01','Active working set/persistence scale','simulation',['F06','H01'],['A19'],[session('RunSession.Ships.cs','AllKnownShips'),session('RunSession.Tick.cs','TickPresentShips'),session('WorldSnapshotAssembler.cs','Build')],['Core/Systems/Travel/ShipRegistry.cs'],'OPEN-11 before budget claims; never erase visits'),
 ('S02','Explicit simulation cursors/policy','simulation',['S01','H02'],['A20'],[system('Travel/ShipRuntime.cs','CatchUp'),system('Travel/ShipInstance.cs','LastSimTime')],['Core/Systems/Travel/ShipSimulationPolicy.cs'],'OPEN-01/03; separate OPEN-04 offline'),
 ('S03','Recovery/resource/age loops','survival',['H01','S02','F09'],['A21','A28'],[system('Survival/VitalsState.cs','Tick'),system('Survival/WoundState.cs','WoundState'),system('Food/SpoilageState.cs','SpoilageState'),session('RunSession.Crafting.cs','RegisterFoodForSpoilage')],['Core/Systems/Food/ResourceFlowLedger.cs'],'OPEN-09 resource/age amendment'),
 ('G01','Versioned reachable generated opportunities','generation',['F02','H02'],['A24'],[ref('Core/Procgen/ShipGenerator.cs','ShipGenerator'),ref('Core/Procgen/FirstRunAwayGate.cs','FirstRunAwayGate'),ref('Runtime/Ship/RuntimeVisualCatalog.cs','RuntimeVisualCatalog')],['Core/Procgen/GameplayOpportunityValidator.cs'],'Physical/damaged/furnished scope and no actual_seed metric'),
 ('G02','Companion release and runtime registration','content_release',['F06','G01'],['A25','A26'],[ref('Runtime/Session/RunSessionHost.cs','CritterProductionLibrary'),ref('Runtime/Session/Views/ThreatCreatureFactory.cs','ThreatCreatureFactory')],[],'OPEN-13 selected release/library; unchanged crawler approval retained'),
 ('S04','Bounded biomass territory/resource','biomass',['H02','S02','S03','G01'],['A22','A23'],[system('Combat/ThreatAIState.cs','ThreatAIState'),system('Combat/SpatialPerceptionState.cs','SpatialPerceptionState')],[],'Conditional OPEN-10 approval; no ML/full autonomy commitment'),
 ('M02','Travel/tow resource extension','mobility',['M01','S03'],['A30'],[session('RunSession.Travel.cs','TravelHome'),system('Travel/SeaGraph.cs','SeaGraph')],[],'Conditional OPEN-09/12 exact cost/connector/tow rules'),
 ('L01','Long-haul release acceptance','acceptance',['P02','H03','M01','S03','G01','G02'],['A14','A15','A16','A17','A18','A19','A20','A21','A22','A24','A25','A27','A28','A29','A30'],[ref('Tests/PlayMode/RunLifecyclePlayModeTests.cs','RunLifecyclePlayModeTests')],[],'Written later plans + chosen campaign/hardware budget; G02/A25 and A28/A30 conditional feature scope explicitly deferred if unselected')]
package_exits={
 'F01':(['A01','A02'],[],'Catalog/static and finite graph units; no normal acquisition claim'),
 'F02':(['A01','A02'],['A11'],'Reviewed definition/source/quantity units; F07 supplies recipe parity and F09 physical witness'),
 'F04A':(['A39'],['A05','A06','A32','A36','A37','A38'],'Early WorkKernelTests/TrainingReceiptTests only with a fake port; live F03 atomic binding and F04/F07/F08 consumers remain downstream'),
 'F03':(['A03','A04','A31','A32','A36'],[],'Model/serialization conservation, atomic faults/UI and paid resolution action; saved/preflight integration waits for F06, earned acquisition F09'),
 'F04':(['A05','A06','A32'],[],'Work record/eligibility/atomic units; saved whole-world Continue phase waits for F06; narrow existing locality correction separable'),
 'F05':(['A07','A08','A34'],[],'Pure supplied-payload storage separable; full actual assembly waits for F03 revision; new schemas F06'),
 'F06':(['A06','A09','A32','A33','A34','A35','A36'],[],'Legacy/provisioned records, unverified craft UI, reachable unknown action/preflight and complete slots; A33 payment preservation only, recipe phases F07; no class journey'),
 'F07':(['A11','A33','A35','A38'],[],'Phase gate/exact paid-input continuation and retained study/legacy read; earned book source/input F09'),
 'F08':(['A02','A37'],['A10'],'Actual utility consumer/raw/XP/work-rest and finite class/order/recovery units; normal first-ship F09'),
 'F09':(['A03','A04','A05','A06','A10','A11','A31','A33','A35','A36','A37','A38'],['A29'],'Normal earned journeys including actual manual/utility/recovery/legacy decision inputs; native first-ship subset. Legacy fixtures are explicitly labeled; full A29 accessibility remains P02/L01')}
amendments={'F02':'amendments/f02-sources.md','F03':'amendments/f03-operability.md','F08':'amendments/f08-class-routes.md'}
delivered={
 'F01':{'commit':'c68c3fe80c322c213b076ffdd1f588596097d1cf','scope':'Bounded on-demand catalog/source validator and actual registration normalization; no launch gate/content acquisition fix','future_scope':'Finite physical sources/exposure/quantities and class route closure'},
 'F04':{'commit':'e062f00386c4ce789dc2a481a5a33568902ff85a','scope':'Existing generic target/context/range and hold release; explicit restore input/HUD; strict repair/seal preserved','future_scope':'Owner-qualified transactional tickets, atomic multi-domain commits and new schema receipts'},
 'F05':{'commit':'52027d3e8cf31a699c4a4f2b94ea0b7cb571462c','scope':'Single-file flushed destination replacement and validated current-shape legacy temp recovery/death/reference scans','future_scope':'Complete multi-payload generations, F03-bound assembly, index/backup process-death recovery and new migration'},
 'F07':{'commit':'15b09d778763c5725c16d56df3244a0ab317d0c0','scope':'Actual effective station tier forwarded through spatial listing/precheck/start and current rejection reasons; no new save fields/content','future_scope':'Knowledge/retained study, paid-input/queue/migration integration and earned upgrades'}}
service_providers={'WorkEligibility':'F04A','WorkTransactionState':'F04A','IWorkCommitPort':'F04A','TrainingEventBus.RecordApplied':'F04A','DomainTransactionCoordinator':'F03','ManualStudyService':'F07','ShipAuxiliaryUtilityState':'F08'}
interface_uses={
 'F03':['WorkEligibility','WorkTransactionState','IWorkCommitPort','TrainingEventBus.RecordApplied'],
 'F04':['WorkEligibility','WorkTransactionState','IWorkCommitPort','DomainTransactionCoordinator'],
 'F07':['WorkEligibility','WorkTransactionState','TrainingEventBus.RecordApplied','DomainTransactionCoordinator'],
 'F08':['WorkEligibility','WorkTransactionState','TrainingEventBus.RecordApplied','DomainTransactionCoordinator']}
work=[]
for wid,title,owner,deps,tids,refs,new,gate in work_rows:
    row={'id':wid,'title':title,'owner_subsystem':owner,'status':'proposed_not_implemented','depends_on':deps,'acceptance_tests':tids,'current_refs':refs,'proposed_files':[PREFIX+p for p in new],'review_gate':gate,'requirements':['REQ-01','REQ-02','REQ-07','REQ-08'] if wid.startswith('F') else ['REQ-01','REQ-03','REQ-04','REQ-05','REQ-06','REQ-08']}
    if wid in package_exits:
        exit_tests,downstream,exit_scope=package_exits[wid]
        row.update({'package_exit_tests':exit_tests,'downstream_integration_tests':downstream,'package_exit_scope':exit_scope})
    if wid in amendments:
        row['review_amendment']='docs/design/formal-build-2026-10-02/'+amendments[wid]
    if wid in delivered:
        row.update({'status':'partially_implemented_bounded_scope','delivered_scope':delivered[wid],'delivery_evidence':'docs/implementation/station-tier-2026-10-02.md' if wid=='F07' else 'docs/implementation/foundation-bounded-2026-10-02.md','full_package_acceptance':'pending'})
    if wid in interface_uses:
        row['interface_dependencies']=[{'service':x,'provider':service_providers[x]} for x in interface_uses[wid]]
    if wid=='F04A':
        row['proposed_modifications']=[{'file':PREFIX+'Core/Systems/Progression/TrainingEventBus.cs','symbol':'RecordApplied','rule':'Record-only log and receipt-aware replay; no XP grant or concrete F07/F08 dependency'}]
    work.append(row)
defaults={'status':'selected_for_provisional_implementation','authority':'Current parent instruction under user authorization to proactively build reviewed gaps','owner_approved_balance':False,'accepted_gameplay':False,'numerical_status':'Tunable hypotheses requiring measured survival and all-class routes; not owner-approved balance','value_sources':['amendments/f02-sources.md','amendments/f03-operability.md','amendments/f08-class-routes.md','contracts.md'],'selections':['F02-A finite supplies and two recovery racks','F03-A separate-health cap and explicit paid legacy overhaul','Four useful auxiliary utility repairs','Retained timed manual study','Conservative legacy Keep paused/Abandon without output-refund/fully paid Start fresh'],'migration_refusal_policy':'Never delete, overwrite or replace the original legacy save; retain exact bytes and parent for later recovery','unchanged_open_policies':['world time','death succession','offline/absent simulation','biomass colony','long-haul economy']}
save('work-packages.json',{'schema':'synaptic-work-plan-2','revision':head,'requirements':list('REQ-%02d'%i for i in range(1,9)),'service_providers':service_providers,'implementation_defaults':defaults,'work_packages':work})

finds=read('docs/audit-deep/content/findings-and-traces.json')
mapping={
 'CONTENT-01':(['F01','F02','F09'],['A01','A02','A03']),
 'CONTENT-02':(['F01','F03','F06'],['A01','A03','A04','A09','A31','A32','A36']),
 'CONTENT-03':(['F01','F02','F08','G01'],['A01','A02','A10','A24']),
 'CONTENT-04':(['F06','F07'],['A11','A33','A35']),
 'CONTENT-05':(['F02','F06','F07'],['A01','A09','A11','A33','A38']),
 'CONTENT-06':(['H01'],['A14']),
 'CONTENT-07':(['F02','S03'],['A02','A21']),
 'CONTENT-08':(['F03','F06'],['A04','A09','A32']),
 'CONTENT-09':(['F07'],['A11']),
 'CONTENT-10':(['F04A','F04','F09'],['A05','A06','A32','A39']),
 'CONTENT-11':(['F08','F09'],['A02','A10','A37']),
 'CONTENT-12':(['F04','F06'],['A06','A09']),
 'GEN-01':(['G02'],['A26']), 'GEN-02':(['G02'],['A25','A26']), 'GEN-03':(['G02'],['A25']),
 'UI-1':(['P01'],['A13']), 'UI-2':(['P01'],['A12']), 'UI-3':(['P01'],['A13']), 'UI-4':(['P02'],['A27']),
 'UI-5':(['S01'],['A19']), 'UI-6':(['S02'],['A20']), 'UI-7':(['H01'],['A14']), 'UI-8':(['P01','P02'],['A13','A27','A29'])}
rows=[]
for f in finds['findings']:
    wid,tids=mapping[f['id']]
    rows.append({'id':f['id'],'title':f['title'],'kind':'audit_finding','evidence_scope':'static_content_with_scoped_fresh_followups','audit_source':'docs/audit-deep/content/content-audit.md','evidence_refs':f['evidence'],'work_packages':wid,'acceptance_tests':tids,'disposition':'adapt','correction':'Cargo seal is a real repair0 producer; all-class approved solo policy applies. Six repair2 model-feasible/five short; no natural walk and no global deadlock claim.' if f['id']=='CONTENT-11' else 'Consult deep README and final v7 scope; latent/disconnected paths are not promoted to normal exploits.'})
for lane,pat in [('generation/findings.md',r'\*\*(GEN-\d+)\s+[-—–]\s+([^*]+)\*\*'),('ui-lifecycle/findings.md',r'^### (UI-\d+)\s+[-—–]\s+(.+)$')]:
    text=(AUDIT/lane).read_text(encoding='utf-8-sig')
    for fid,title in re.findall(pat,text,re.M):
        wid,tids=mapping[fid]
        rows.append({'id':fid,'title':title.strip(),'kind':'audit_finding','evidence_scope':'source_confirmed_or_scoped_model_diagnostic_as_reported','audit_source':'docs/audit-deep/'+lane,'evidence_refs':[],'work_packages':wid,'acceptance_tests':tids,'disposition':'adapt_or_conditional_release','correction':'Exact crawler still approved; source/build/receipt/registration are separate. No reapproval.' if fid.startswith('GEN') else 'Native Title/caption/audio/long-haul performance not certified by source/fixture.'})
integrated=[
 ('INTEGRATED-01','Acquisition closure',['F01','F02','F03','F08'],['A01','A02','A03','A10']),
 ('INTEGRATED-02','Original-home service identity',['H01'],['A14']),
 ('INTEGRATED-03','Internal/exterior structural semantics',['H02','H03'],['A15','A16','A22']),
 ('INTEGRATED-04','Work locality and input restore',['F04','F06'],['A05','A06']),
 ('INTEGRATED-05','Visitation-dependent simulation',['S02'],['A20']),
 ('INTEGRATED-06','Save replacement recovery',['F05','F06'],['A07','A08','A09','A34']),
 ('INTEGRATED-07','Offered launch/accessibility/lifecycle',['P01','P02'],['A12','A13','A27','A29']),
 ('INTEGRATED-08','Companion adoption/decision freshness',['G02'],['A25','A26']),
 ('GAP-01','Occupied/nested detach and earned mobility',['M01'],['A17','A18']),
 ('GAP-02','Meaningful survival/resource/cargo journeys',['S03','L01'],['A21','A28']),
 ('GAP-03','Physical furnishing/multideck/perception bounds',['G01','H02'],['A15','A22','A24']),
 ('GAP-04','Future biomass/logistics choices',['S04','M02'],['A23','A30'])]
for fid,title,wids,tids in integrated:
    rows.append({'id':fid,'title':title,'kind':'integrated_finding' if fid.startswith('INTEGRATED') else 'explicit_unknown_or_open_scope','evidence_scope':'bounded_deep_audit_not_campaign_acceptance','audit_source':'docs/audit-deep/README.md','evidence_refs':[],'work_packages':wids,'acceptance_tests':tids,'disposition':'adapt_or_decision_gated','correction':'No native/campaign or optional mechanics commitment inferred from diagnostics.'})
negmap=[(['F07'],['A11']),(['F04'],['A05','A06']),(['F07'],['A11']),(['F03'],['A04']),(['F03','F06'],['A03','A09']),(['S03'],['A28'])]
for f,(wids,tids) in zip(finds['negative_findings'],negmap):
    rows.append({'id':f['id'],'title':f.get('title',f.get('claim','Retained negative')),'kind':'retained_negative','evidence_scope':'reported_current_source_or_model_negative','audit_source':'docs/audit-deep/content/content-audit.md','evidence_refs':f.get('evidence',[]),'work_packages':wids,'acceptance_tests':tids,'disposition':'preserve_with_regression','correction':'Do not replace a functioning path with an absence claim.'})
diagmap={
 'component_identity_transfer_power_coupling':(['F03'],['A03']), 'component_identity_transfer_reactor_console':(['F03'],['A03']),
 'production_start_restore_outage':(['H01','S03'],['A14','A21']), 'craft_station_outage_guard':(['F07','H01'],['A11','A14']),
 'nested_stowed_payload':(['M01'],['A17']), 'atmosphere_scalar':(['H02','H03'],['A15','A16']),
 'session_work_hold_restore':(['F04','F06'],['A06']), 'session_actual_travel_cost':(['M02'],['A30']),
 'disk_prewrite_failure':(['F05'],['A07']), 'session_generic_cut_work_locality':(['F04'],['A05']),
 'module_boundary_representation':(['H02'],['A15']), 'session_secondary_shelter_services':(['H01'],['A14']),
 'known_ship_tick_save_scale':(['S01'],['A19']), 'staged_delete_move_save_recovery':(['F05'],['A08']),
 'real_launch_validation':(['P01'],['A12']), 'normal_supply_component_command_boundary':(['F02','F09'],['A02','A03']),
 'compiled_internal_exterior_cuts':(['H02'],['A15'])}
for d in read('docs/audit-deep/runtime/results-v7/model-results.json')['results']:
    wids,tids=(['S02'],['A20']) if d['id'].startswith('elapsed_') else diagmap[d['id']]
    rows.append({'id':'DIAG-'+d['id'],'title':d['hypothesis'],'kind':'provisioned_diagnostic_trace','evidence_scope':d['scope'],'audit_source':'docs/audit-deep/runtime/results-v7/model-results.json','diagnostic_id':d['id'],'evidence_refs':[],'work_packages':wids,'acceptance_tests':tids,'disposition':'retain_discriminator_not_acquisition_pass','correction':d['boundary']})
save('traceability.json',{'schema':'synaptic-audit-trace-1','revision':head,'rows':rows})
with (OUT/'traceability.csv').open('w',newline='',encoding='utf-8') as f:
    writer=csv.DictWriter(f,fieldnames=['id','title','kind','evidence_scope','audit_source','work_packages','acceptance_tests','disposition','correction'])
    writer.writeheader()
    for r in rows:
        writer.writerow({k:';'.join(r[k]) if isinstance(r.get(k),list) else r.get(k,'') for k in writer.fieldnames})

ledger=read('docs/audit-deep/content/dependency-ledger.json')
missing=set(ledger['summary']['recipe_ingredients_outside_upper_bound_closure'])
blocked=set(ledger['summary']['recipes_blocked_by_source_upper_bound'])
recipes=[]
for r in ledger['recipes']:
    recipes.append({'recipe_id':r['recipe_id'],'audit_definition':r,'source_blocked_upper_bound':r['recipe_id'] in blocked,'missing_input_ids':[i for i in r['ingredients'] if i in missing],'proposed_disposition':'review_required_source_or_explicit_defer' if r['recipe_id'] in blocked else 'retain_potential_source_and_require_finite_physical_witness','work_packages':['F01','F02','F07'],'acceptance_tests':['A01','A02','A11'],'gate':'OPEN08 exact source/knowledge amendment; normal witness before required exposure','natural_acquisition_certified':False})
save('catalog-dispositions.json',{'schema':'synaptic-catalog-plan-1','revision':head,'review_amendment':'docs/design/formal-build-2026-10-02/amendments/f02-sources.md','amendment_selected':None,'recipe_count':62,'source_blocked_count_upper_bound':42,'missing_input_ids':sorted(missing),'items_without_enumerated_source':ledger['summary']['registered_items_no_enumerated_source'],'component_forms_without_registered_definition':ledger['summary']['undefined_referenced_items'],'recipes':recipes})
classrows=read('docs/audit-deep/content/predeparture-repair-producers.json')['rows']
starts={r['class_id']:r for r in ledger['summary']['starting_classes']}
save('class-bootstrap.json',{'schema':'synaptic-class-bootstrap-plan-1','revision':head,'approved_policy':'Every starting class can establish a home and repair a first ship solo; meaningful skills/tools and specialization remain.','review_amendment':'docs/design/formal-build-2026-10-02/amendments/f08-class-routes.md','amendment_selected':None,'baseline_evidence':'docs/audit-deep/content/predeparture-repair-producers.json','work_packages':['F08','F09'],'acceptance_tests':['A02','A10'],'rows':[{'class_id':r['class'],'title_fresh_available':not r['title_locked'],'initial_skills':starts[r['class']]['starting_skills'],'finite_cache_model':r,'planned_status':'earned_route_required_not_yet_certified','normal_traversal_witness':False,'normal_requirement':'Legitimate unlock state if locked; no hub repair/XP perks, no grants, finite inventory and normal order variants'} for r in classrows]})
print(json.dumps({'work_packages':len(work),'planned_tests':len(tests),'trace_rows':len(rows),'recipes':len(recipes),'classes':len(classrows),'preservation_files':len(read('docs/design/formal-build-2026-10-02/preservation-baseline.json')['files'])}))
