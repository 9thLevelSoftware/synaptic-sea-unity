# Ownership and interface contracts

**Status: proposed contracts with separately attributed bounded delivery.** Existing public methods remain compatibility adapters while their callers migrate. C# 9-compatible engine-free services use existing `GdDict`/`GdArray`, `Vec3`, `IStorage` and resource loaders at serialization boundaries. The catalog validator row below is an existing API; the other new services are reserved proposals.

## Identifiers and authorities

`run_id`, `ship_id`, `component_instance_id`, `job_id`, `container_id`, `connection_id`, `station_id` and `snapshot_generation` are stable opaque strings. A room/module/slot ID is local and is always qualified by `ship_id`. `definition_id` identifies immutable content, never a unique object. Seeds remain decimal strings where integer precision would be lost. `revision` is a nonnegative monotonic owner-state integer and is saved.

| Authority | Writers | Readers/projections | Prohibited implicit ownership |
| --- | --- | --- | --- |
| Production catalog release | Content loader/validation once at boot/import | Inventory, recipe gates, generation, UI | UI grants or silent ID aliases |
| Item instance registry | Validated inventory/component transaction | Bags/cargo/placement/modification summaries, weight/capability UI | Both slot and bag owning one component |
| Vessel service state | Vessel-scoped machinery/production commands | Session active context, habitat HUD, craft stations | `AwayFromStart` or `HomeShip` deciding all service existence |
| Work job | Work service/session adapter | Work HUD and snapshot | Runtime physical button serialized as consent |
| Knowledge/progression | Explicit learning/training commits | Recipe and skill-tree gates, summaries | Null knowledge implying all learned |
| Mechanical graph | Connection and travel transaction | Load, transforms, membership/navigation | Shared air/power inferred from membership |
| World clock/cursors | Simulation coordinator under saved policy | Hazards/production/catch-up | Wall-clock silently modifying in-run time |
| Commit manifest | Persistence coordinator after payload validation | Continue/slot index/load/repair | Index entry becoming proof of a durable payload |

## Foundation service signatures

Files are under `SynapticSea/Assets/_Project/Core/Systems/` unless their row starts `Core/Session/`. CatalogSourceValidator.Validate/LoadProductionCatalog/NormalizeSources already exist in the bounded F01 delivery; its future source/exposure integration remains pending. Other service files/shapes below are proposed. A returned `GdDict` conforms to the records below; no untyped success defaults are permitted. Narrow engineering does not imply complete implementation of these contracts.

| Current or proposed file/service | Exact public shape | Consumes / produces |
| --- | --- | --- |
| `Infra/CatalogSourceValidator.cs` (existing bounded F01) | `GdDict Validate(GdDict catalog, GdDict sourceGraph, GdDict exposureManifest)` | Existing normalized catalog/source diagnostics; proposed release/exposure coverage extends this same validator |
| `Inventory/ItemInstanceState.cs` | `GdDict Get(string instanceId)`; `GdDict GetSummary()`; `bool ApplySummary(GdDict summary)` | Unique equipment state keyed by instance; rejects duplicate holders/nonfinite state |
| `Inventory/ComponentTransferService.cs` | `GdDict Prepare(GdDict command, GdDict domainSnapshot)` | Produces a validated candidate only; never mutates registry, inventory, placement, machinery or XP |
| `Core/Session/DomainTransactionCoordinator.cs` | `GdDict Prepare(GdDict command)`; `GdDict Commit(string transactionId)`; `GdDict GetSummary()`; `bool ApplySummary(GdDict summary)` | Sole mutation owner for component/work/craft bundles: registry, placement, inventory, machinery, jobs, XP and receipts publish atomically; failure leaves prior bundle intact |
| `WorkActions/WorkEligibility.cs` | `GdDict Evaluate(GdDict ticket, GdDict context)` | Stable target, current player/owner state and input mode -> reasoned eligibility |
| `WorkActions/WorkTransactionState.cs` | `GdDict Begin(GdDict ticket, GdDict context)`; `GdDict Advance(string jobId, double delta, GdDict context)`; `GdDict Commit(string jobId, GdDict context)`; `GdDict Interrupt(string jobId, string reason)`; `GdDict GetSummary()`; `bool ApplySummary(GdDict summary)` | Early F04A bounded work kernel delegates via IWorkCommitPort; F03 binds the sole DomainTransactionCoordinator publisher. No concrete downstream service dependency |
| `Crafting/RecipeGateService.cs` | `GdDict Evaluate(string recipeId, string phase, GdDict context)` | Preview/start checks ingredients; advance/complete use persisted paid-input receipt, not another ingredient set. Shared identity/tier/knowledge logic has explicit phase requirements |
| `Save/SaveCommitCoordinator.cs` | `GdDict Commit(GdDict payloads, string runId, string slotId)`; `GdDict Recover(string runId, string slotId)` | Complete bound payload bundle; frozen/death authority checked before selecting any generation |
| `Core/Session/SavePayloadAssembler.cs` | `GdDict Build(RunSession session, string slotId, string slotKind)` | Captures one revision for run/world/all referenced layouts and metadata; every gameplay slot family uses this complete bundle |
| `Core/Session/BootstrapRouteValidator.cs` | `GdDict Evaluate(string classId, GdDict scenario)` | Finite reachable producers, exact XP/objective ordering and costs -> witnessed route or minimal unmet dependency set |
| `Inventory/LegacyEquipmentReconciliationService.cs` | `GdDict Inspect(string instanceId, GdDict context)`; `GdDict PrepareOverhaul(string instanceId, GdDict context)` | Exact old evidence or candidate paid compatibility overhaul; no invented prior health, standalone mutation or XP |
| `Progression/ManualStudyService.cs` | `GdDict Begin(string bookId, GdDict context)`; `GdDict Resume(string jobId, GdDict context)` | Retained manual, character+book read identity and 30 eligible seconds; completion delegates the atomic owner and existing BooksRead authority |
| `ShipSystems/ShipAuxiliaryUtilityState.cs` | `GdDict Get(string fixtureId)`; `GdDict GetSummary()`; `bool ApplySummary(GdDict summary)` | Ship-qualified physical hardware/effect state, revision/progress and per-character training receipt; not objective system health |

Existing `InventoryState.AddItem(string, long)`, `CargoTransfer.MoveItem`, `ComponentPlacementState.Dismount(string)` / `Mount(string, string, string, long, GdDict, ComponentCatalog)`, `CraftingState.CanCraft` / `BeginCraft`, `WorkActionDriver.Tick`, `SaveLoadService.SaveToSlot` and `LoadFromSlot` adapt to these services. Change compatibility signatures only after all callers/tests are inventoried. `UniqueItemState` records collectibles/codex claims, so it is not repurposed as equipment identity storage.

## C6 shared-service delivery order

F04A delivers the engine-free WorkEligibility/WorkTransactionState kernel, IWorkCommitPort and TrainingEventBus.RecordApplied before F03's paid-overhaul consumer. The new `Core/Systems/WorkActions/IWorkCommitPort.cs` contract has `GdDict Prepare(GdDict command)` and `GdDict Commit(string transactionId)`. WorkTransactionState is constructed with `WorkTransactionState(IWorkCommitPort commitPort)` and has no concrete DomainTransactionCoordinator or RunSession dependency. An absent port returns `commit_port_unbound` without effects; production composition cannot use a fake/default port.

In F03, DomainTransactionCoordinator implements that port as the sole atomic publisher. Work kernel Begin/Advance/Interrupt operate on job state cloned/staged by that owner; no adapter may publish progress or completion into an independent live store. Early F04A fixtures exercise detached summaries and a recording/rejecting fake port only. F04 retains its downstream dependency on F03 and performs the existing action/session/HUD integration; F03 never waits on F04 for its kernel.

F04A also modifies existing `Core/Systems/Progression/TrainingEventBus.cs` with `void RecordApplied(GdDict eventRecord, string commitId)`. It validates matching event/receipt identity, logs an already-applied event and never calls Emit/GrantXp. Receipt-owned replay is skipped when progression totals were restored; conflicting duplicate receipt payloads reject. F03 stages progression, this log and receipt together. F07 study and F08 utilities consume this early primitive; neither implements a prerequisite for the other. A39 covers record-only XP invariance and replay using cloned state before those consumers exist.

## Record contracts

| Record | Required fields / validation |
| --- | --- |
| Catalog entry | `id`, `kind`, `schema_version`, `source_path`; resolved tool capabilities, carried form, positive finite component mass, legal stack/category, production exposure state |
| Source edge | `edge_id`, `producer_id`, `consumer_id`, `kind` (loot/pickup/repair/salvage/craft/production/learn/train), AND input IDs+quantities, outputs, tool/skill/tier/knowledge/access preconditions, finite budget, live registration path, persistence owner, diagnostic flag |
| Component instance | schema_version=1, instance_id, definition_id, item_form, condition_state=known/unknown; condition finite [0, 1] when known and JSON null when unknown; positive finite mass, holder, origin, provenance and revision. Unknown is not zero health and cannot supply condition-dependent capability |
| Transfer command | `command_id`, `instance_id`, expected source/destination holder and revisions, target slot/capacity; cannot select an anonymous same-form count |
| Prepared transfer | transaction_id, validated revisions, full candidate domain bundle, conservation assertions and expected identity. Only DomainTransactionCoordinator publishes it; adapters never apply independent deltas |
| Work ticket | `job_id`, `action_id`, `actor_id`, `ship_id`, `target_id`, target revision, local anchor and `range_m`, LOS policy, `input_mode` (hold/toggle/accessibility), duration/progress, material policy, output owner, committed effect IDs |
| Work context | Actual player/ship identity, current transform/LOS, tool/skill/inventory revision, stamina/wounds, power, live input. Evaluate on begin, every advancement and immediately before commit |
| Work effort policy | Action-definition-owned eligible-duration basis, progress speed rule, stamina debit rule and rest/interrupt behavior, persisted by definition hash. Utility/recovery/overhaul use ordinary work effort; study uses stationary eligible time with no industrial drain. An adapter cannot silently substitute wall-clock or the default work drain |
| Source requirement disposition | Explicit consumed inputs versus retained tools/manuals and once-only character+book learning receipt. A retained book cannot be counted as a consumed ingredient or unlimited repeat-XP source in finite route solving |
| Eligibility/result | `ok`, stable `reason`, owner/target revision, detailed missing requirements; commit result includes `committed`, `commit_id`, applied quantities/instances and effects. Same commit ID returns its prior result |
| Recipe context | Explicit phase, station ID/kind/owner/tier/power, actor inventory/skill, nonnull knowledge, catalog release and paid-input receipt. Preview/start validate current ingredients; completion never debits/requires them again |
| Paid craft input | job_id, recipe_id/definition hash, owner/station/actor IDs, payment_commit_id, exact consumed quantities, input_state=paid, resolved start skill/tier/knowledge/quality, remaining time and completion_commit_id. Begin atomically consumes one set and publishes this receipt; save/restore preserves it |
| Legacy craft reconciliation | stable reconciliation_id, preserved original active/queue/station record+source hash, payment_state=unverified, status=paused_unverified, player disposition and decision receipt. Never equivalent to a paid-input receipt |
| Study record | job_id, character_id, book_id, required carried holder, progress/30 eligible seconds, paused reason and read_commit_id; manual quantity is never consumed. BooksRead and knowledge/XP commit together |
| Legacy overhaul | exact instance/owner/source evidence or raw 1+1 payment; progress/30 nominal eligible seconds; result known0.60 and provenance paid_legacy_overhaul with previous condition=null retained in receipt; no machinery-health mutation/XP |
| Bootstrap scenario | Launch tuple, class/unlock provenance, finite contents/tool placements, objectives with force-repair effects, XP multipliers/fractions, costs, no-meta flag, source/traversal witness references and terminal predicates |
| Save manifest | schema_version, generation/parent, run/slot/kind IDs, domain_revision, run/world/all layout hashes+versions, current owner/location/pose, catalog/clock/cursors, death/freeze references and committed status; complete matched bundle only |

No resources flow from an unreachable closed inventory or another vessel unless the player performs an authorized cargo transfer. Inventory weight remains the current soft encumbrance model; unique-item reservation changes stack/holder accounting without silently imposing a new hard weight cap.

## Failure semantics

### Sole domain transaction owner

`DomainTransactionCoordinator` owns one committed `DomainBundle` reference and revision on the simulation thread. It is the only writer of participating instance/holder, inventory, placement/modification, linked machinery, job/paid-input, progression/training-log and completion-receipt state. Prepare clones participating state into a candidate, validates all source/destination revisions and conservation, and stages every mutation there. Commit validates again and publishes one new bundle reference containing all fields and its receipt; it never asks UI/session adapters to apply returned deltas independently. Any failure before publication discards the candidate. After publication, notification/audio/HUD failures are presentation failures: retry reads the existing receipt and cannot repeat domain/XP effects.

Snapshot assembly, input commands and state readers see only the old or new committed bundle. Existing session model fields become owner-controlled projections/accessors refreshed from that same bundle; UI stops mutating passed models. No snapshot or observer runs between per-field staging steps. If a legacy in-place adapter must remain temporarily, the coordinator owns all before-images and rollback, suppresses observers/saves during application and tests each rollback boundary; it is not a second transaction owner. Prefer cloned-state publication for the new paths.

Failure tests inject before/after registry, placement, bag/cargo, machinery, job/payment, progression/training log, receipt staging and immediately before publication. Every prepublication failure preserves the complete original bundle/hash and emits no success; postpublication notification failure preserves the complete new bundle and one receipt/output/XP. Repeated Commit and restart/Continue tests exercise the same transaction IDs. UI drag, panel install, timed mount and craft start/completion all submit commands to this owner.

### Instance interaction contract

Existing files: `UI/Panels/InventoryPanel.cs`, `UI/Panels/ShipModificationPanel.cs`, `Core/Systems/Inventory/InventorySelectionModel.cs`, `Game/SessionUiBridge.cs`, `RunSession.WorkAction.OnShipModInstalled/OnShipModUninstalled` and component markers. Proposed session API: `GdArray ListComponentInstances(string holderId)`, `GdArray ListInstallTargets(string instanceId)`, `GdDict RequestComponentInstall(string instanceId, string shipId, string slotId)`, `GdDict RequestComponentTransfer(string instanceId, string destinationHolderId)`. These submit to DomainTransactionCoordinator and never select by anonymous form count.

Unique UI rows use key `instance:<instance_id>` and show name, known condition percentage or Unknown, mass and holder/installed destination. Stack rows use `stack:<item_id>`. Transfer/split/equip dispatch distinguishes these keys; unique equipment cannot split or be selected by aggregate form quantity. ShipModificationPanel replaces first-preferred-form installation and fabricated hub-slot discovery with current vessel's actual target list. It emits instance-qualified commands and displays compatibility/tool/skill/power/access/range reasons without mutating ShipModificationState. Empty physical mount anchors and focused installed machinery provide an Install/Remove prompt that opens the same target/instance picker; keyboard/gamepad Submit and mouse use the same command path.

ListInstallTargets reports actual owner-qualified slots, component slot_kind/role rules, vacancy, machinery link, required tool/skill, local access/range/LOS and power budget. Incompatible targets are displayed with reasons and cannot commit. Two reactor_console instances at 0.23 and 0.81 can be selected separately and installed into distinct compatible slots; the chosen one remains chosen through live list reorder. Unknown-condition cargo can transfer but installation returns `condition_unknown` until evidence resolves it under the reviewed policy.

### Phase-aware recipe gates

Evaluate phases are `preview`, `start`, `advance`, `complete`. Preview/start require a known recipe, actual tier/skill/knowledge and exactly one ingredient set; start atomically consumes that set and commits the paid receipt/job. Power can start paused under the retained ordinary craft policy. Advance requires the same job/owner/station and paid receipt, pauses for power loss, and never checks/consumes another ingredient set. Complete verifies receipt identity/hash, unchanged recipe definition compatibility, station/owner existence, completion time and output capacity; no ingredient, skill or knowledge re-acquisition test is imposed on an already paid job. Skill/tier/quality resolved at payment is persisted; changing equipment/settings cannot reroll quality. Output-full completion stays pending with its receipt until output is accepted, rather than dropping output or charging again.

An unpaid malformed new job rejects nonmutating. Legacy active_craft shape and matching station summary cannot prove payment: FinishCraft/StationState.FinishAndAdvance can auto-start a queued entry without ConsumeIngredients and serialize that same shape. Therefore neither old progress/quality nor a zero remaining ingredient count establishes payment. Every legacy active/queued job without an independently valid payment receipt becomes paused_unverified, preserving its exact record. No legacy_paid inference, automatic advancement/output/refund or quality reuse is allowed. Duplicate completion of a genuinely new paid job returns its receipt outcome. Exact-one-recipe inventory succeeds through begin -> zero remaining inputs -> outage -> Continue -> complete once. Production knowledge cannot be null at preview/start; completion uses persisted start/payment evidence.

### Legacy craft reconciliation UI

RecipePickerPanel, its IRecipePickerHost adapter, SessionUiBridge and RunSession.Crafting expose a persistent Old job needs review card and a normal Inventory/Jobs entry. Explain: Materials for this old job could not be verified. It is paused; no items have been produced or returned. Choices are Keep paused, Abandon old job (no output/refund) and Start fresh with listed materials. Start fresh explicitly warns that old inputs may already have been used, retains the old record in the reconciliation archive, and creates a new job through current gates with one newly paid ingredient set/full craft time/new start quality. It never pays only for remaining time or automatically charges on load. Player choice and archive persist atomically; repeat confirmation cannot create duplicate jobs. Archived/quarantined jobs do not auto-reserve a station or block unrelated new crafting. Original save bytes/parent remain available. Absence of materials leaves the old job paused; no completion grant hides the deficit. Actual UI input and reload tests must cover both a paid historical BeginCraft and an unpaid queued auto-start that produce the same serialized shape.

### Legacy equipment reconciliation in F03/F06

Implement the actual Inspect/Overhaul UI and mechanical surface action in the same migration delivery, using amendments/f03-operability.md: wrench/repair 0/raw scrap x1+wiring x1/30 nominal eligible seconds/known0.60 newly earned/zero XP. Unknown remains null until exact evidence or a committed paid result. F06 preflight checks independent safe access, tools, finite source budget and essential unknowns before publishing any converted generation. A resolution that depends on the unknown equipment's own power/access/departure is invalid; missing essentials return legacy_resolution_unreachable and keep original save/live state unchanged. Conversion cannot defer this action to later balance work.

Ordinary detachment changes link availability without DamageSubcomponent(..., 1.0) or panel damage0.6. Mount/load never applies the0.55 floor. Actual destructive removal is an unchosen separate rule; existing fire/combat damage retains its own callers.

### Retained manual study

InventoryPanel adds Study/View manual for an actual carried book, through SessionUiBridge; no generic Use-all consumes books. ManualStudyService uses a character_id derived stably from the saved run/player identity, without choosing death succession. Study takes 30 eligible stationary seconds with no industrial stamina drain, progresses only while the copy is carried and the actor can act, and displays progress/pause/Already studied. Movement/copy loss/damage interrupts; manual release and Continue pause until explicit resume. World time/hazards and ordinary stationary vitals recovery continue.

Completion stages PlayerProgressionState.GrantXpFromBook and RecipeKnowledgeState.LearnFromBook together, retaining the manual. BooksRead is the existing per-character idempotent set; character+book receipt suppresses repeat XP/learning across duplicate copies/reload. Present book/skill definitions are validated before calling GrantXpFromBook, which otherwise can mark an invalid target read. Legacy BooksRead=true seeds mapped knowledge without GrantXpFromBook/XP replay; possession or skill totals do not infer a read. Proposed TrainingEventBus.RecordApplied(GdDict eventRecord, string commitId) logs already-applied service/book events without granting XP; receipt-owned records cannot be granted again by ReplayInto after restored progression totals.

| Operation | Interrupted/rejected behavior |
| --- | --- |
| Loot transfer | Debit only accepted units; persist remainder in the container. Empty only when remainder is zero |
| Component dismount/install | Output capacity and destination slot checked before mutation; unavailable destination leaves original holder intact; no anonymous drop |
| Held manual work | Release pauses progress without effects; moving out of range/LOS or target invalidation interrupts. Explicit resume reacquires eligibility |
| Accessibility toggle work | Continues without held button while physically eligible; restored job still awaits explicit resume |
| Crafting | Current normal input-at-start consumption remains explicit; power loss pauses and output commits once. Foundation disables unused unsafe queue path until each queued entry follows normal gates/consumption |
| Repair/sealing | Keep current strict-range cancellation and completion-time part checks; no automatic remote cargo borrowing |
| Damage/exhaustion/death | Interrupt with actual cause; no component/material completion or XP replay; terminal policy unchanged |
| Snapshot restore | Validate all owners/instances first in a temporary world. Restore jobs paused with zero held input. Publish live state only after complete graph validation |
| Asset/profile absence | Preserve exact saved identity and report missing compatibility; authorized placeholders remain explicit; do not reroll |

Automatic craft cancellation/refund is not generalized onto repair or salvage. Each ticket declares when resources commit. New reservation behavior needs conservation tests and migration. Component transactions default to final-commit consumption; current craft cancellation behavior remains labeled until a separate UX/refund decision is approved.
