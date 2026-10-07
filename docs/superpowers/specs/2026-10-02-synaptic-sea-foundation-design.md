# Synaptic Sea foundation: acquisition and reliable state

**Status:** revision 3 C6 foundation addendum, with C1-C5 independently cleared and reviewed defaults selected provisionally. Audit 840f14f remains separate from bounded a52a347 and tier commit 15b09d7. Early F04A work/receipt primitives precede F03 overhaul/F07 study; A37/A38 Unity fixture paths/runners/assertions are explicit. Numerical amounts/durations/XP/earned condition are tunable survival/all-class hypotheses, not owner-approved balance or accepted gameplay. See [bounded closure](../../design/formal-build-2026-10-02/review-closure-v3.md). This task writes no product code.

## 1. Deliverable and boundaries

Deliver an earned source-to-state chain that future systems can trust: acquire a usable tool, remove and carry a damaged component, transfer/install without condition laundering, perform work only at the correct site, save safely, resume explicitly, and show truthful recipe gates. All supported classes must have a finite solo route to functional shelter and a repaired first ship.

This is not a complete long-haul economy release. New owned-vessel production, shared atmosphere/power, expanded generated hulls, offline advancement and new biomass AI belong to subsequent subsystem gates. The foundation preserves local propulsion and current mechanical joins; it does not promise full earned assembled-home flight until M01/L01 acceptance.

## 2. Audited baseline discriminators

| Fact | Implementation consequence |
| --- | --- |
| Wrench/tool_wrench absent from normal definitions/sources; 11 forms unregistered | Catalog existence and normal tool acquisition must precede component-transfer claims |
| 42/62 recipes source-blocked even under permissive closure; 17 missing ingredients; 31 registered items lack enumerated sources | Required exposed content needs explicit source placement or deferral; graph reachability cannot be inferred from row existence |
| `CraftingStation.TryCraftRecipe` and `ListStationRecipeEntries` use default tier 0 | Share exact effective-tier and knowledge context with actual `BeginCraft` |
| `RecipeKnowledgeState` unowned by session; 12 books not items; sole book recipe is `craft_thruster_nozzle` | Add explicit session/save authority and actual learning callers; define the nozzle's book mapping |
| `TickWorkAction` only rechecks locality for three home actions; restore sets `_workRequiresHold=false` | Stable owner-qualified tickets and explicit restore-paused state |
| `FileSystemStorage.WriteText` deletes destination before moving temp; staged valid temp not recovered | Single-file durability and multi-payload consistency must be fault-tested separately |
| Cargo seal consumes one of six `hull_sealant`, earns actual repair XP 15 | Preserve this real finite bootstrap; `sealant` is a different ID; no assumed fire farming |
| Current finite-cache matrix reaches repair 2 for six classes; five short; security ordering matters | Close remaining finite routes and protect training opportunities without a universal skill grant |

Evidence status and current diagnostic IDs are in [evidence](../../design/formal-build-2026-10-02/evidence.md). These rows do not promote provisioned probes into normal acquisition passes.

F01 bounded validator, F04 existing locality/hold/restore-input and F05 single-file recovery are now implemented/independently approved. Baseline observations above remain historical; current APIs/evidence are separately attributed. No new instance/atomic/migration/content or complete-generation scope is marked complete. Tier forwarding is independently delivered at 15b09d7. The empty-container anchor diagnostic does not qualify the final production descriptor/root/occupancy or earned walking.

## 3. F01/F02: catalog and source integrity

Use the current merged `ItemDefs.LoadDefinitions` input list and exact audited ledger as the baseline. Normalize once and validate tool capabilities, all 11 carried forms, recipe ingredients/outputs, repair parts, deconstruction yields, crops, books, skill prerequisites, station kinds/tier sources, role-set components and live generator/authored supplies.

Tool identity: choose one registered canonical `wrench` item, with explicit `unbolt`/`mount` capability. `tool_wrench` becomes a migration alias only if old saves contain it; reject definitions that expose two conflicting tools. Proposed normal producer is a reachable finite maintenance tool placement independent of a wrench-required salvage path. F02-A finite supplies/recovery are selected provisionally; exact home/generated placement remains a qualified content amendment under OPEN-08, not a resource grant. Existing crowbar/cutter/welder requirements remain meaningful.

All carried forms receive registered inventory views: category `component`, max stack 1 for legacy form compatibility, positive catalog mass and display name. The category is explicitly haulable in `CargoTransfer`; instance-bearing forms never merge by ID. Catalog mass is authoritative; validation rejects conflicting duplicate authored mass.

For every exposed required recipe, nominate a live source chain. Recipes that are intentionally future content are excluded from normal pickers and have a documented defer reason and future work owner. No blanket claim that all 62 recipes must be simultaneously available at first launch. `catalog-dispositions.json` lists each exact recipe and source gap so omission cannot hide a disconnected chain.

The graph separates potential source, live registered producer, physical traversal witness and finite route feasibility. A loop such as coolant requiring coolant needs an external seed source; a craft/deconstruct cycle is not free duplication without quantitative net analysis. Check every AND input and quantity, tool-access dependencies, prerequisite closure, station/tier/knowledge sources and seeded cycles. Graph failure reports the exact minimum unmet IDs and producer/caller path. Provisioned diagnostic edges cannot discharge production requirements.

## 4. F03: inventory and component identity

F04A shared work/receipt primitives precede this package. WorkEligibility/WorkTransactionState depend only on the early IWorkCommitPort; F03 binds it to the sole atomic DomainTransactionCoordinator. F04 remains downstream session integration. RecordApplied is also early F04A work before F07 study, with no F08 prerequisite.

Retain `InventoryState.Items` for fungible stacks. Add `ItemInstanceState` and adapters for player/ship/cart/slot holdings; unique component selection uses instance ID. `UniqueItemState` remains collectible/codex claims. Display projected counts for compatibility, but make the instance registry the only equipment owner.

Prepare dismount with current component, source vessel/slot, condition and output capacity. DomainTransactionCoordinator is the sole mutation owner: it stages instance, placement, bag/cargo, machinery, job, XP and receipt in one candidate bundle and publishes atomically. Adapters/UI never independently apply returned deltas. Rejecting any boundary preserves the old complete bundle; postcommit presentation failure cannot replay effects. Prepare install against target/tool/skill/compatibility and debit that exact instance while preserving condition/mass/origin. Equal local slots cannot collide. Reinstall is not repair: use the selected provisional F03-A mapping in [F03 amendment](../../design/formal-build-2026-10-02/amendments/f03-operability.md); never issue the old minimum 0.55 heal during mount/load.

Do not invent a general drop system to hide overflow. Initial transfers may reject capacity cleanly. Existing work yield drops need stable world IDs and persistence before they may receive failed component outputs. Loot overflow retains unaccepted units in the original container with searchable remainder; debits only accepted units as `CargoTransfer` already does. The soft weight/encumbrance model remains.

Package A03/A04 checks reactor_console condition 0.23 through A/bag/cargo/B/A, injected failures at every domain boundary, and persistent-state serialization conservation. F03 also adds real instance selection in InventoryPanel/ShipModificationPanel, bridge commands and discoverable physical install targets: two same-form items at 0.23/0.81 must be individually selectable through mouse/keyboard/gamepad and show actual compatibility reasons. Continue/revisit integration waits for F06; earned UI acquisition waits for F09. Invalid power_coupling remains a rejection diagnostic, not a twelfth component.

## 5. F04: work locality, held input and interruption

At start create an owner-qualified `WorkTicket` from the actual interactable. Retain the action's current range, strict/inclusive comparator and LOS probe in the ticket; do not pick a new universal distance. Evaluate at begin, every advancement and immediately before commit. A ticket never resolves against `CurrentShip` alone. Context switch, unload, owner/target revision change or lost sight/range interrupts with `left_work_site`/`target_changed` as appropriate.

Hold input release pauses without work, stamina debit, noise or effects. Accessibility/toggle continues only while eligible. Load restores both as paused awaiting explicit resume, with zero held input. Explicit resume validates tool/material/skill/owner/range again. Runtime settings change may change input mode through a deliberate transition; it never makes an invalid site eligible. Damage/exhaustion preserves specific existing denial causes. Death uses current terminal behavior.

DomainTransactionCoordinator owns item/condition/structure, machinery, progression/training log and receipt application in one atomic bundle. WorkTransactionState delegates commit to that owner; it cannot apply independent deltas. Inject failures before/after each staged domain and before publication. Persist bounded receipts so retry cannot issue output/XP twice. Existing repair/seal strict rechecks remain. Reviewed narrow locality/hold correction may proceed independently of this later atomic bundle integration.

Ordinary crafts retain current explicit input-at-start consumption and powered pause/resume. The unused `EnqueueCraft`/auto-start path is disabled or rejects with `unsupported_queue` until each queue entry is fully validated and consumes inputs. Do not expose a new cancel/refund UI without its own material policy.

## 6. F05/F06: save recovery and migration

Implement [persistence rules](../../design/formal-build-2026-10-02/persistence.md) before writing new identity/job/knowledge summaries into live saves. Full F05 payload assembly depends on F03 committed DomainBundle/revision; the narrowly delivered supplied-payload storage work remains separable. Proposed next versions are `gate2-current-run-7` and `world-5`, with nested component/work/knowledge schemas at 1. Reserve them once when implementation starts; confirm no concurrent work used those names.

Run/world/layout commits form one generation. Validate staging payloads and hashes, publish a commit record only after durability checks, retain parent generation and reconstruct index metadata from valid manifests. A lone temp is recoverable only if its complete identity/schema/reference set is valid; never pick the newest arbitrary JSON. Migration clones and validates, preserves original bytes and never mutates a live world until all ownership/graph constraints pass.

Legacy mounted exact condition/identity stays. Unknown carried forms remain null-tagged until exact evidence or shipped paid compatibility overhaul produces newly earned 0.60 via wrench/repair 0/raw 1+1/30 nominal work seconds/zero XP. Essential unreachable tools/surface/budget/safe access block conversion and preserve originals; cargo serialization alone cannot close F06. Complete slots/frozen guards remain per persistence. Legacy active/queued craft shape cannot prove payment and is preserved paused_unverified with explicit Keep paused/Abandon no refund/Start fresh fully paid UI.

## 7. F07: truthful tier and knowledge gates

Use RecipeGateService.Evaluate(recipeId, phase, context), with preview/start/advance/complete phases. Preview/start validate one exact ingredient set, effective tier/skill/non-null knowledge/owner and atomically publish consumed inputs plus a paid job receipt. Advance/complete validate that persisted receipt and station/job identity, not a second ingredient set; power outage pauses, output-full completion stays pending and output commits once. Start quality/eligibility snapshots persist without reroll. Exact-one-recipe inputs -> empty inventory -> outage -> Continue -> one output is mandatory. Tier1 sensor/tier 2 nozzle retain actual installed-tier checks at start; source acquisition is separate.

Session owns RecipeKnowledgeState and retained timed manual study. InventoryPanel Study/View requires an actual carried copy, 30 stationary eligible seconds/no industrial stamina drain, interrupts/resumes explicitly and commits existing BooksRead+mapped knowledge+authored book XP once per character/book. Copies remain. Legacy BooksRead seeds knowledge without XP replay; possession does not. No instant consume/use-all book route. Nozzle mapping stays fabrication_schematic_basic with actual fabrication4/tier 2/materials.

Keep current fabrication-vs-portable skill policy initially, with accurate UI. OPEN-07 governs station-specific cooking/pharmacology changes later. `CanCraft` with null knowledge is prohibited in production, while historical model tests can use explicit diagnostic knowledge contexts.

## 8. F08: every-class solo bootstrap

Functional first home means a claimed usable shelter with an actually safe local air/hazard state, storage, portable care/recovery and a reachable supply/repair route. Sustainable non-original-home production is H01/S03 work. First ship means a locally powered, repaired, controllable craft that performs departure and return under current capability checks. Neither predicate requires immediately flying a welded large home.

Acceptance requires witnessed routes for all 11 class definitions; fresh Title uses the eight available classes, and the other three need legitimate unlock provenance. No meta repair/XP perks, timer-expired home safety, respawning caches, grant helpers, arbitrary fire or remount farming. Route search uses finite quantities and actual fractional/class XP, objective side effects and survival costs. `restore_systems` can remove battery-cell practice; route feasibility must cover normal orders, preserving or replacing finite practice rather than requiring hidden prior knowledge.

Corrected baseline: engineer/mechanic/scientist begin above first gate; pilot and salvage_captain reach it by preserving repairs; security can reach repair 2 after early battery/star-chart repairs and cargo seal. Cook/field_medic get repair 1 via cargo seal then star_charts. Medic, cook, communications, field_medic and signal_specialist remain below repair 2 in this specific finite-cache model. Those deficits are planning input, not proof of global deadlock.

The concrete F08 amendment names fabrication-feed/cargo-relay/medical-light/dock-beacon defects in ship-owned auxiliary state, outside objective repair mappings, with real consumer/view effects. Dedicated raw 1+1/crowbar/repair 0/12 nominal-work/direct 60 XP adapter preserves existing RepairPoint25 XP/part-category behavior. Ordinary effort/wounds/stamina/rest, record-only logs and finite two-rack recovery are required. All-class arithmetic/order checks remain diagnostics until earned F09; no farming/refill or universal recovery from lethal/exhaustive choices.

## 9. Acceptance and review

F09 requires normal cache/tool acquisition, actual component start/removal/transfer/install, held release/damage/move interruption, real save/Continue/revisit, class-bootstrap journeys, and one repeated expedition check that distinguishes finite depletion from resupply. No provisioned route can close a natural gate. Existing class and source tests stay diagnostics; Play/native evidence must record input method and exact build/source identity.

Foundation subsystem exit requires A01..A11, A31..A39 and a real native first-ship/save/revisit input witness. A29 also includes full accessibility checks owned by P02; the foundation witness alone cannot close its caption/layout portions. A12/A13/A27 are early presentation deliveries P01/P02 and must pass before a player release claims those controls and lifecycle behaviors. Companion A26 belongs to G02. Full program long-haul L01 passes the later habitat/mobility/survival/generation/native gates for its selected feature scope, with A23/A28/A30 explicitly conditional on their reviewed decisions. Any unresolved content amendment blocks its dependent feature release; it does not block unrelated safe service implementation. Review all proposed records/versions/route boundaries before product implementation.
