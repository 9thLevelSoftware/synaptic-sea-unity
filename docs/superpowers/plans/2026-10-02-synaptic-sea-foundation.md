# Synaptic Sea Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans for later implementation after written contract review, within the parent-authorized scope. This documentation task executes neither product implementation nor delegation.

**Revision 3 C6 addendum:** audit 840f14f remains immutable. Prior bounded baseline a52a347 and independently delivered tier commit 15b09d7 are separate from future atomic/instance/generation work. C1-C5 are independently cleared; the remaining C6 correction introduces early F04A work/receipt primitives and explicit A37/A38 Unity runners/assertions. Source/utility/study/overhaul/legacy policies are selected provisional defaults. All numbers are tunable hypotheses requiring survival/all-class evidence; actual production placement remains unqualified. This task edits documentation only.

**Goal:** Establish earned acquisition and reliable item/work/save state so every supported class can make a home and repair a first ship solo.

**Architecture:** Add focused engine-free services around existing Core models. Session/UI adapt through common ownership, eligibility and transaction records; preserve useful systems and compatibility loaders. Source routes and provisioned state diagnostics have separate acceptance.

**Tech Stack:** Unity6000.6.0f1, C#9-compatible Core, GdDict/GdArray, NUnit3, .NET8 Core runner; existing asmdefs and PowerShell wrappers.

**Spec:** [Foundation design](../specs/2026-10-02-synaptic-sea-foundation-design.md), [contracts](../../design/formal-build-2026-10-02/contracts.md), [persistence](../../design/formal-build-2026-10-02/persistence.md).

## Global Constraints

- Every starting class can establish a home and repair a first ship solo through earned resources and meaningful specialization.
- Returning and finishing onboarding continue the same life; no extraction-victory loop.
- Preserve existing saves, exact ship/component/creature identity and proprietary assets.
- Existing InventoryState.Items stays fungible stack authority; components have one unique instance/holder.
- Unknown/newer/malformed save state rejects before live mutation; migration preserves original bytes.
- Restore work paused with zero held input; explicit resume revalidates locality and requirements.
- Reserve proposed gate2-current-run-7/world-5 only after confirming no intervening use.
- C1-C5 are independently cleared; this C6 addendum receives the final targeted contract review. F02-A and named utility/study/overhaul/legacy defaults are selected provisionally; numerical acceptance needs measured survival/all-class evidence, not another owner balance approval.
- No remote operations; local commits only if separately requested. Existing audits/profiles remain untouched.

## Review Focus

- Legacy anonymous forms/equal local slot IDs: never duplicate or repair through migration (F03/F06, A03/A09).
- Full destination/container: conserve unaccepted finite units and component holder (F03, A04).
- Released input/site/context/save changes: no remote completion or automatic resume (F04/F06, A05/A06).
- Mixed generations/future malformed save: complete old/new load or nonmutating failure (F05/F06, A07/A08/A09).
- Objective order/locked classes/null knowledge: no perk or hidden-order/unrestricted default (F07/F08, A10/A11).

---

## Execution conventions

Paths below omit prefix `SynapticSea/Assets/_Project/`; data paths omit `SynapticSea/Assets/StreamingAssets/data/`. Create paths are proposed. Every new signature is defined in contracts, and structured result fields are mandatory. Current public APIs stay adapters until callers migrate. Metadata follows existing Unity conventions. Existing fixture bytes/expected old schemas remain unchanged; add versioned cases.

Core Tests/EditMode cases run in tools/dotnet/SynapticSea.Core.Tests/SynapticSea.Core.Tests.csproj. First run `dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter FullyQualifiedName~CLASS` and assert expected case names/nonzero discovery, then filtered execution/nonzero count. UI Toolkit Tests/EditModeUnity requires Unity SynapticSea.Tests.EditModeUnity; Play requires SynapticSea.Tests.PlayMode. Core cannot discover those assemblies. Use scoped Unity runner/distinct XML and verify intended cases/nonzero execution; tools/test.ps1 runs Core first and overwrites generic logs. Red means intended behavioral failure, not compile/path/zero-discovery failure. Never overwrite audit outputs.

Concrete later implementation commands, from this checkout: create a new evidence directory and change run-01 for every attempt. These use the actual Unity CLI syntax in tools/test.ps1 without its Core-first wrapper. This documentation task does not execute them.

```powershell
New-Item -ItemType Directory -Force artifacts/foundation-v3-review/run-01
dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter 'FullyQualifiedName~ClassBootstrapRouteTests'
dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter 'FullyQualifiedName~ClassBootstrapRouteTests' --logger 'trx;LogFileName=class-routes.trx' --results-directory artifacts/foundation-v3-review/run-01
dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter 'FullyQualifiedName~PaidCraftStateTests'
dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter 'FullyQualifiedName~PaidCraftStateTests' --logger 'trx;LogFileName=paid-crafts.trx' --results-directory artifacts/foundation-v3-review/run-01
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" test "F:\tmp\synaptic-sea-playable\SynapticSea" --mode EditMode --filter 'SynapticSea.Tests.Unity.ComponentInstancePanelTests' --output "F:\tmp\synaptic-sea-playable\artifacts\foundation-v3-review\run-01\component-ui.xml"
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" test "F:\tmp\synaptic-sea-playable\SynapticSea" --mode PlayMode --filter 'SynapticSea.Tests.PlayMode.FoundationAcquisitionPlayModeTests' --output "F:\tmp\synaptic-sea-playable\artifacts\foundation-v3-review\run-01\foundation-play.xml"
```

The Unity names are required proposed fixture namespaces matching existing Unity/Play conventions. Check intended fullnames and nonzero executed cases from XML; skipped-only/zero-case is not PASS. Core discovery and execution are separate required checks. Use exclusive Unity project access; launch any background helper hidden. Archive exact source/build/profile/provisioning and every attempt.

## F01: catalog and source validator

**Files/current delivery:** CatalogSourceValidator.cs and CatalogSourceValidatorTests.cs already exist after c68c3fe. LoadProductionCatalog/NormalizeSources and RunSession.GetCatalogSourceRegistrations/GetCatalogValidationReport feed DependencyValidator.VerifyCatalogSources. Preserve those delivered APIs and diagnostic-only launch behavior. Extend those existing files and ItemDefs/composition only under reviewed source/exposure scope; proposed exposure data is integration/content_exposure.json. Do not create a competing validator.

**Interfaces:** Consume normalized real catalogs, typed source graph and exposure manifest. Produce `GdDict Validate(GdDict catalog, GdDict sourceGraph, GdDict exposureManifest)` with exact errors/dispositions/coverage.

- [ ] Write `ProductionReferencesRequireDefinitionsAndRegisteredProducers`: separately delete wrench/form/gauze/book producer and assert exact stable error and caller/input path. `DiagnosticEdgesDoNotSatisfyProduction` rejects grant-only source; `SeedlessAndInputCycleRejects` rejects coolant-only bootstrap while distinguishing reagent-spending deconstruction.
- [ ] Preserve passing delivered CatalogSourceValidatorTests; run new source/exposure cases for intended behavioral failure, with nonzero discovery.
- [ ] Extend existing normalization/Validate and DependencyValidator registration coverage with reviewed quantity/tool/tier/knowledge/AND inputs and finite caveats. Preserve delivered load/report APIs; do not recreate the validator or its already fixed failures.
- [ ] Run new tests plus ItemDefsTests/ShipSystemsCatalogTests. PASS requires every row classified; it does not claim normal physical reachability.
- [ ] Review exact diff and archive validator report/current source identity.

## F02: source/tool/form amendment

**Files:** Modify `tools/tool_definitions.json`, `components/component_catalog.json`, `items/item_definitions.json`, `items/loot_tables.json`, `player/skill_books.json`, `recipes/recipe_definitions.json`; actual home producer in `Core/Session/RunSession.Build.cs` and generated finite supplies in `Core/Procgen/GameplaySliceBuilder.cs`. Modify ItemDefs form normalization, CargoTransfer category allowlist and RunSession.WorkAction tool predicate. Create `Tests/EditMode/Systems/Infra/AcquisitionSourceTests.cs`. Prepare `docs/design/formal-build-2026-10-02/amendments/f02-sources.md` before data edits.

**Interfaces:** Consume F01 and all 62 catalog dispositions. Produce canonical wrench/unbolt/mount capability, all 11 positive-mass component inventory views and selected source/learning rows; tool_wrench is legacy alias only.

- [ ] Use the selected provisional F02-A amendment and verify its finite quantities/book-nozzle mapping and clear anchor contract. Publish actual local anchor IDs/role/caller witnesses before edits; approved solo policy is not reopened.
- [ ] Write `WrenchHasReachableNonWrenchProducer`, `EveryComponentFormHasCatalogMassAndHaulCategory`, `RequiredExposedRecipesHaveSeededSources`, `BooksAndGauzeHaveRegisteredLiveSources` against amendment values and actual callers.
- [ ] Run AcquisitionSourceTests red check.
- [ ] Implement only reviewed rows/adapters. Preserve tool/skill gates, finite cache, soft weight; six hull_sealant remains distinct from sealant. Unique components never merge by form.
- [ ] Run F01/F02 tests and all 62 recipes through actual loaders; record chosen source witnesses and deferred exposure.

## F04A: early shared work and receipt primitives

**Files:** Create Core/Systems/WorkActions/IWorkCommitPort.cs, WorkEligibility.cs, WorkTransactionState.cs, Tests/EditMode/Systems/WorkActions/WorkKernelTests.cs and Tests/EditMode/Systems/Progression/TrainingReceiptTests.cs. Modify existing Core/Systems/Progression/TrainingEventBus.cs for RecordApplied and receipt-aware replay. These are documentation-required future paths, not product edits in this task.

**Dependencies/interfaces:** F01 only. Deliver WorkEligibility.Evaluate and WorkTransactionState constructor/methods through the declared IWorkCommitPort; no concrete F03/F04/F07/F08 references. Isolated fixtures use recording/rejecting fake ports and cloned summaries. Live composition waits for F03's sole DomainTransactionCoordinator binding; F04 stays downstream for ordinary session integration. RecordApplied is a record-only method before F07 study, not F08 work.

- [ ] Write kernel owner/range/LOS/hold/effort/progress/restore tests and CommitPortUnboundCannotPublish/RejectedPortKeepsPriorCandidate. RecordAppliedDoesNotGrantXp, RestoredReceiptLogDoesNotReplayXp and ConflictingAppliedReceiptRejects use cloned progression/log state, not ManualStudyService/utility fixtures.
- [ ] Discover and run both A39 Core fixtures separately with nonzero named cases and new TRX paths. No compile error or empty filter can count as red/green.
- [ ] Implement detached kernel/port and record-only receipt filtering. Prepare/Commit publication remains the F03 port owner's responsibility; no fallback mutation path.
- [ ] Keep existing WorkActionChannel/driver and ordinary training replay tests passing. Report A39 as primitive units, never earned survival or actual UI/atomic completion.

```powershell
dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter 'FullyQualifiedName~WorkKernelTests'
dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter 'FullyQualifiedName~WorkKernelTests' --logger 'trx;LogFileName=work-kernel.trx' --results-directory artifacts/foundation-c6/run-01
dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter 'FullyQualifiedName~TrainingReceiptTests'
dotnet test tools/dotnet/SynapticSea.Core.Tests --nologo --filter 'FullyQualifiedName~TrainingReceiptTests' --logger 'trx;LogFileName=training-receipts.trx' --results-directory artifacts/foundation-c6/run-01
```

## F03: component and finite loot conservation

**Files:** Create Core/Systems/Inventory/ItemInstanceState.cs, ComponentTransferService.cs, LegacyEquipmentReconciliationService.cs, Core/Session/DomainTransactionCoordinator.cs, Core/Session/Interactables/LegacyOverhaulPoint.cs; Core ComponentTransferTests/DomainTransactionTests/LootRemainderTests/LegacyEquipmentReconciliationTests and Unity ComponentInstancePanelTests. Modify existing InventoryState/ShipInventory/CargoTransfer/InventorySelectionModel/placement/resolver/modification/machinery, RunSession.WorkAction/LootContainer, InventoryPanel/ShipModificationPanel/SessionUiBridge. Connect ordinary Inspect/Overhaul UI and passive surface in this delivery.

**Interfaces:** Consume F02 component/form views and early F04A WorkEligibility/WorkTransactionState/IWorkCommitPort. ItemInstanceState Get/GetSummary/ApplySummary; ComponentTransferService.Prepare(GdDict command, GdDict domainSnapshot) returns a candidate only. DomainTransactionCoordinator Prepare/Commit/GetSummary/ApplySummary alone owns registry/placement/inventory/machinery/jobs/XP/receipts. Session instance/target list and install/transfer request signatures are in contracts; UI never mutates models.

- [ ] Write DamagedReactorConsolePreservesIdentityAcrossTwoShips (0.23 vs0.81, A/bag/cargo/B/A), EqualLocalSlotIdsDoNotCrossWrite, FullDestinationLeavesMountedComponent, UnacceptedLootRemainsInSerializedRemainder and AtomicDomainFaultBoundaries. Inject every registry/placement/bag/machinery/job/XP/receipt/publication boundary; complete old/new bundle, no intermediate snapshot or replay.
- [ ] Unity ComponentInstancePanelTests selects same-form parts via mouse/keyboard/gamepad, reorders rows, discovers compatible/incompatible physical slots and asserts exact instance commands/no model writes. Actual acquisition stays F09.
- [ ] Run ComponentTransferTests/LootRemainderTests red check.
- [ ] Implement sole coordinator/read models/UI/remainders and reviewed same-delivery legacy resolution: wrench/repair 0/raw 1+1/30 nominal seconds/new condition 0.60/zero XP. F03-A detach changes availability: remove actual dismount DamageSubcomponent(link, 1.0) and panel0.6 as well as remount/load0.55. Keep combat/fire damage. Test saved machinery 0.8/0.2 unchanged through detach/reinstall/Continue.
- [ ] Run Core A03/A04/A32 plus retained resolver/cargo tests and Unity A31 separately with discovery checks. F03 unit exit excludes real Continue/revisit, which depends on F06.
- [ ] Record compatibility/condition mapping and focused ownership review.

## F04: work locality and completion once

**Files/current delivery:** e062f00 already implements ValidateWorkSite/RestoreWorkAction/ResumeRestoredWork with existing WorkLocalityTests/WorkRestoreTests, WorkUiIntegrationTests and WorkSessionPlayModeTests. Extend these rather than recreating/regressing delivered cases. F04A now owns the early WorkEligibility/WorkTransactionState; this downstream F04 owns atomic home/repair/seal session adapters.

**Interfaces:** Consume F04A WorkEligibility/WorkTransactionState/record primitives after their early plan step and F03 DomainTransactionCoordinator. Keep F03 as a dependency; this package integrates session/HUD/adapters and does not create the earlier kernel. WorkTransactionState delegates through IWorkCommitPort to the sole owner. Admitted narrow locality/hold correction is separable; full atomic completion remains review-gated.

- [ ] Write `GenericCutStopsOutsideItsSavedSite` using 1,414 m model discriminator; `ReleaseHoldDoesNotSpendStaminaOrProgress`, `ToggleStillRequiresSite`, `ContextChangeCannotWriteEqualModuleId`, `DuplicateCommitDoesNotGrantOutputOrXp`.
- [ ] Run WorkLocalityTests red check.
- [ ] Implement each action's current range/strictness/LOS at begin/advance/commit. Release pauses; invalid site/damage/exhaustion interrupts; retain strict repair/seal completion checks and specific reasons.
- [ ] Run A05/A06, settings-change/tool/material-loss variants and WorkActionChannelTests; no context fallback or output replay.
- [ ] Record material/effect policy for every adapted action; keep session as composition.

## F05: durable save generations

**Files:** Create Core/Systems/Save/SaveCommitCoordinator.cs, Core/Session/SavePayloadAssembler.cs, Tests/EditMode/Systems/Save/SaveCommitRecoveryTests.cs and Tests/EditMode/Session/SlotPayloadBindingTests.cs. Modify FileSystemStorage.WriteText in Core/Services/Storage.cs, SaveLoadService/SaveIndexState/TitleSaveQuery/PermadeathResolver, RunSession.Save, UI/Presenters/SaveSlotScreenModel.cs, UI/Menus/MenuCoordinator.cs and Game/SessionUiBridge.cs callbacks. Extend SaveLoadServiceTests.

**Interfaces/dependencies:** Full F05 waits for F03 DomainBundle/revision before SavePayloadAssembler.Build(RunSession, string slotId, string slotKind) and actual gameplay slots. Pure supplied-payload storage can proceed after F01. FileSystemStorage publication/SaveLoadService.RecoverLegacyTemporary are delivered by 52027d3, and SaveCommitRecoveryTests already exist. Extend with complete generations/frozen guards; new field integration stays F06. Plain-payload disk units cannot close actual gameplay assembly.

- [ ] Write StagedValidLegacyTempIsRecoveredWhenDestinationAbsent, FailedWriteKeepsPreviousGeneration, RunWorldMismatchNeverLoadsMixedState, IndexFailureRebuildsFromCommittedManifest, EverySlotFamilyBindsCompletePayload and FrozenRunCannotRecoverPreDeathParent. Exercise corrupt index/death/valid ancestor, manual restore and explicit new-run slot reuse in MemoryStorage/disposable FileSystemStorage.
- [ ] Keep delivered SaveCommitRecoveryTests green; add focused new generation/assembly/frozen cases with meaningful red discovery. Do not expect the fixed staged legacy-temp regression to fail again.
- [ ] Implement staging/validation/manifest/pointer/index with previous generation retention. Verify Windows durable flush/replacement semantics through official documentation during implementation.
- [ ] Fault-inject every payload/manifest/pointer/index boundary, locked files/full writes and real restart. A07/A08 PASS means complete old/new state or explicit nonmutating failure.
- [ ] Record fault matrix/disposable root/hash and confirm no user/audit save mutation.

## F06: schema migration and paused restore

**Files:** Modify snapshots/migration/ShipInstance/assemblers/RunSession.Save and extend existing parity tests. Create Tests/EditMode/Systems/Save/LegacyInstanceMigrationTests.cs and Tests/EditMode/Session/PaidCraftStateTests.cs, LegacyCraftReconciliationTests.cs, LegacyEquipmentReconciliationTests.cs. Actual RecipePickerPanel/IRecipePickerHost, InventoryPanel, SessionUiBridge and RunSession.Crafting expose reconciliation/resolution. PaidCraftStateTests creation is shared with F07.

**Interfaces:** Consume F03/F04 committed bundles and F05 generations; produce run7/world5 with known/unknown condition tag and nullable value, paid-input/start-quality receipts and paused jobs. Unit exit A06/A09/A32/A33/A34 uses provisioned/legacy schemas; F08/F09 class journeys are downstream.

- [ ] Confirm version availability. Add old run1..6/world families, missing optional fields, malformed/future records, anonymous forms and aliased machinery migration tests.
- [ ] Run LegacyInstanceMigrationTests/SessionSaveParityTests red check for new cases.
- [ ] Implement clone/migrate/validate/preflight/staged apply. Unknown stays null until exact evidence or shipped paid overhaul; essential missing safe route/tool/surface/budget refuses conversion and preserves original bytes. Legacy active/queue shape cannot prove payment: preserve paused_unverified/archive and actual Keep paused/Abandon no refund/Start fresh new payment+full time UI. Valid new receipts never recharge; death authority precedes recovery.
- [ ] Run held-work/migration/hash/parity cases plus LegacyPaidAndUnpaidQueueShapesBothRemainUnverified, LegacyFinishedShapeCannotProduceOrRefund, LegacyDecisionSurvivesContinue, UnknownOverhaulUsesNormalInputAndFiniteSources and EssentialUnknownUnreachableKeepsOriginalSave. Test actual UI separately from provisioned schemas; assert no XP/health/output replay.
- [ ] Review schema ADR/defaults and retain old fixture bytes.

## F07: truthful tier and knowledge

**Files:** Create Core/Systems/Crafting/RecipeGateService.cs, Core/Systems/Progression/ManualStudyService.cs, Core RecipeGateParityTests/PaidCraftStateTests(shared F06)/ManualStudyTests and Tests/EditModeUnity/ManualStudyPanelTests.cs (A38). Modify CraftingState/RecipeKnowledgeState/PlayerProgressionState.BooksRead, InventoryPanel/RecipePickerPanel/SessionUiBridge, work effort profile and save/assemblers. Preserve independently delivered 15b09d7 actual-tier forwarding; it does not implement knowledge/paid-phase gates. Book mappings stay in actual catalogs.

**Interfaces:** Consume F02/F06 knowledge/payment schemas, sole domain owner and upstream F04A WorkEligibility/WorkTransactionState/TrainingEventBus.RecordApplied. No study prerequisite is delivered by F08. Produce Evaluate(string recipeId, string phase, GdDict context): preview/start checks one ingredient set; advance/complete uses paid receipt, never a second set. Start quality persists; output-full stays pending.

- [ ] Write TierTwoNozzlePhaseGatesUseStartPayment for nozzle tier 2/skill4/book: one set becomes zero at start; outage/Continue then exactly one output/no second charge/quality reroll. Add TierOneSensorUsesEffectiveTier, NullKnowledgeRejectsStart, PaidCompletionNeedsNoSecondIngredients, OutputFullRetainsPaidJob, LearnedBookSurvivesContinue and UnsafeQueueRejectsWithoutOutput.
- [ ] Run RecipeGateParityTests red check independently of provisioned ingredients.
- [ ] Implement phase gates and retained 30-second Study/View with actual carried manual, stationary eligibility/no industrial stamina drain/explicit interruption+Continue resume and atomic BooksRead+knowledge+existing authoredXP once per character/book. Legacy BooksRead seeds knowledge with zero additionalXP. Add RetainedManualStudyPausesAndCommitsOnce/LegacyBooksReadSeedsKnowledgeWithoutXp and actual UI cases. Disable unsafe queue; preserve current discipline policy.
- [ ] Run A11, RecipeKnowledgeStateTests/CraftingStateTests and actual save parity; normal book acquisition stays F09.
- [ ] Record source mapping and avoid unreviewed OPEN-07 discipline changes.

## F08: finite class solo routes

**Files:** Create Core/Session/BootstrapRouteValidator.cs, Core/Systems/ShipSystems/ShipAuxiliaryUtilityState.cs, Core/Session/Interactables/AuxiliaryServicePoint.cs, Runtime/Session/Views/AuxiliaryUtilityView.cs, Core ClassBootstrapRouteTests/HomeUtilityRepairTests. Modify ShipInstance summary/session Build+Interact+WorkAction+Crafting, InteractionRegistry with doc parity, CargoHoldControl/InventoryPanel manual cargo adapter and consume the F04A TrainingEventBus.RecordApplied/receipt-aware replay. Create Tests/PlayMode/AuxiliaryUtilityViewPlayModeTests.cs for A37 actual views. Source placements require actual qualification; preserve classes.json and ordinary RepairPoint25 XP/part checks.

**Interfaces:** Consume F01/F02/F07 gates, F06 saves and F04A record-only training/work primitives; this consumer never supplies RecordApplied to study. Produce Evaluate(string classId, GdDict scenario) route/unmet set, never a physical acceptance claim.

- [ ] Use the four selected useful hardware repairs; qualify their actual owners/effects/physical candidates. The empty-container normal-startup diagnostic is bounded evidence; final authored descriptor/root/occupancy qualification remains pending. Dedicated raw-quantity adapter: crowbar/repair 0/strict1.8 m/LOS/12 nominal work seconds/main stamina+wound/rest path/atomic raw 1+1/direct repair 60 XP plus record-only log. Rest25->75 is a route policy, not a refill. Validate fractional class multipliers, actual cargo seal15 and all 11 ordinary objective orders.
- [ ] Write `EveryCatalogClassHasFiniteSoloBootstrap`, `SecurityObjectiveOrdersRetainTrainingRoute`, `CookCargoSealBootstrapUsesActualHandler`, `FreshMetaDoesNotGrantRepairPerks`, `LockedClassesRequireUnlockProvenance`. Exercise actual wrappers/objective force-repairs.
- [ ] Run ClassBootstrapRouteTests red check and report finite/source/skill/order failures separately from traversal unknowns.
- [ ] Implement real fabrication-feed/cargo-relay/medical-light/dock-beacon consumers and runtime on/off views; unused flags cannot pass. Preserve objective independence and per-character fixture XP receipt. After ordinary primary4+4 spending, recover two finite racks8+8 through actual crowbar work/noXP; measure utility4+4 and essential-overhaul budget. No refill or universal recovery after every lethal/exhaustive choice.
- [ ] Run A02 finite class/model/wrapper/order checks and serialized receipt interruption. F08 package exit excludes A10's normal Title/first-ship journey, which is run under F09; natural_traversal_witness stays false until then.

## F09: earned journey and evidence

**Files:** Create `Tests/PlayMode/FoundationAcquisitionPlayModeTests.cs`, `ClassBootstrapPlayModeTests.cs`; extend actual PlayableScenePlayModeTests/RunLifecyclePlayModeTests as appropriate. New results under distinct docs/playtest directory; existing audit/checkpoints untouched.

**Interfaces:** Consume F01..F08. Produce fresh normal acquisition/class/order/interruption/Continue/revisit manifest, with native input evidence A29 separate from batch Play.

- [ ] Write Title -> physical tool/cache -> salvage/cargo/install -> safe home/first craft -> interrupted save/Continue -> revisit checks, with actual XP/clamped health/debits and no grants.
- [ ] Run the scoped Unity PlayMode runner for FoundationAcquisitionPlayModeTests with exclusive project access/new disposable profile/distinct outputs; verify intended names/nonzero execution. Account for Core-first/generic-log wrapper behavior described above. Diagnose controller/lock/path failures before changing balance.
- [ ] Run eight fresh classes and legitimately unlocked classes, common objective orders and two basic excursions. L01 three varied trips/campaign review remains later scope.
- [ ] Run `pwsh tools/test.ps1 -Mode Dotnet`, then scoped Unity/Edit/Play for affected seams. Inspect skips/no-grant flags; skips cannot close release gates.
- [ ] Record exact source/content/schema/build/input/provisioning, failures and limits. Await later scoped design/plan review; no publication.

## C6 Unity fixtures and green-run XML assertions

Create A37 `SynapticSea/Assets/_Project/Tests/PlayMode/AuxiliaryUtilityViewPlayModeTests.cs` in namespace SynapticSea.Tests.PlayMode. Bind actual ShipAuxiliaryUtilityState, local power, CargoHoldControl/manual transfer, Light/repeater view and DockingManager/airlock projections. Four named cases below must observe committed consumer/view changes, not an unused bool. Core HomeUtilityRepairTests remains the raw/XP/effort/recovery unit fixture.

Create A38 `SynapticSea/Assets/_Project/Tests/EditModeUnity/ManualStudyPanelTests.cs` in namespace SynapticSea.Tests.Unity. Exercise actual InventoryPanel Study/View and SessionUiBridge/session command path, pause/reload/explicit resume, retained copies, BooksRead/knowledge/read receipts and no repeated XP. Core ManualStudyTests remains separate. These Unity fixtures can provision isolated state and must label it; F09 alone supplies earned source/input journeys.

Use exclusive project access, a fresh attempt directory and matching Unity assemblies. The following commands/assertions are for later green implementation runs; this task runs no Unity. Assert intended cases and Passed results from XML after each runner exits. Missing/zero/skipped-only target fixtures fail even if the process exits zero.

```powershell
New-Item -ItemType Directory -Force artifacts/foundation-c6/run-01
function Assert-UnityFixtureXml {
    param([string]$Path, [string]$Fixture, [string[]]$ExpectedCases)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing Unity XML: $Path" }
    [xml]$result = Get-Content -LiteralPath $Path -Raw
    $cases = @($result.SelectNodes('//test-case') | Where-Object {
        ([string]$_.fullname).StartsWith($Fixture + '.')
    })
    if ($cases.Count -eq 0) { throw "Zero discovered/executed cases: $Fixture" }
    foreach ($name in $ExpectedCases) {
        $matches = @($cases | Where-Object {
            ([string]$_.fullname) -eq ($Fixture + '.' + $name) -or
            ([string]$_.fullname).StartsWith($Fixture + '.' + $name + '(')
        })
        if ($matches.Count -eq 0) { throw "Missing intended case: $Fixture.$name" }
    }
    $bad = @($cases | Where-Object { ([string]$_.result) -ne 'Passed' })
    if ($bad.Count -gt 0) { throw "Failed/not executed target cases: $Fixture" }
    Write-Output "$Fixture : $($cases.Count) executed and passed"
}
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" test "F:\tmp\synaptic-sea-playable\SynapticSea" --mode PlayMode --filter 'SynapticSea.Tests.PlayMode.AuxiliaryUtilityViewPlayModeTests' --output "F:\tmp\synaptic-sea-playable\artifacts\foundation-c6\run-01\utility-views.xml"
if ($LASTEXITCODE -ne 0) { throw "Utility-view Unity runner failed" }
Assert-UnityFixtureXml -Path artifacts/foundation-c6/run-01/utility-views.xml -Fixture 'SynapticSea.Tests.PlayMode.AuxiliaryUtilityViewPlayModeTests' -ExpectedCases @('FabricatorFeedProjectsCommittedPower','CargoRelayEnablesBulkCommandsPreservesManualAccess','MedbayLampProjectsUtilityWithoutHealing','DockBeaconShowsActualDockAndAirlockState')
& "$env:LOCALAPPDATA\Unity\bin\unity.exe" test "F:\tmp\synaptic-sea-playable\SynapticSea" --mode EditMode --filter 'SynapticSea.Tests.Unity.ManualStudyPanelTests' --output "F:\tmp\synaptic-sea-playable\artifacts\foundation-c6\run-01\manual-study-ui.xml"
if ($LASTEXITCODE -ne 0) { throw "Manual-study Unity runner failed" }
Assert-UnityFixtureXml -Path artifacts/foundation-c6/run-01/manual-study-ui.xml -Fixture 'SynapticSea.Tests.Unity.ManualStudyPanelTests' -ExpectedCases @('CarriedManualOpensTimedStudyAndView','InterruptedStudyAndContinueRemainPaused','RepeatedCopiesAndLegacyReadDoNotAwardAgain')
```

Required scenario detail: feed follows hardware AND actual power without tier/output grants; relay changes real bulk-command availability while manual physical transfer remains; medical lamp changes rendering without healing; docking repeater follows actual dock/airlock state without opening doors. Study UI retains the manual and shows timed/pause/Already studied state through real commands. XML evidence includes exact source/build/profile/provisioning; never reuse an audit output.

## Handoff

This plan covers every foundation requirement and Review Focus mode. Later service/topology/time/companion/UI scope lives in the roadmap. New names match contracts; machine checker verifies current anchors. Reviewed source/XP defaults are selected provisionally, with tunable numbers and required measured qualification. Early F04A prerequisites and A37/A38 Unity evidence are explicit; finish this targeted C6 review before dependent dispatch. Broader open policies and later scoped review gates stay unchanged.
