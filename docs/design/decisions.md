# Design decisions

Product and design choices that code and tests must not silently undo. `OPEN` items are questions for the product owner; they are never settled by tuning a test harness.

## Decided

| ID | Decision |
| --- | --- |
| D1 | The baseline is the newest worktree, after it passes the Phase 0 stabilization gate. |
| D2 | The camera is Project Zomboid-style isometric 3D. Keep and extend `Runtime/Player/IsoCameraRig.cs` and `InteriorOcclusion.cs`. |
| D3 | AI agents (Claude, Codex) execute the plan in PR-sized packages under the guardrails of the master plan. |
| D4 | The priority is the long-haul sandbox: persistent world depth, homes, ship assembly and flight. |
| D5 | Fix the economy with all three levers: authored early caches, better home production, and slower attrition through a game-time scale. |
| D6 | Universal basic repair. Every class can repair slowly at low quality; specialists are faster and better. This satisfies REQ-07. |
| D7 | Project Zomboid controls: WASD plus mouse facing and aim, right-click context menus on world objects, a visual map and moodle icons. E stays as a quick-interact shortcut. |
| D8 | Death ends the survivor, not the world. The corpse, home and stashes stay, and the player starts a new survivor in the same persistent world. Roguelite meta-payout and save freezing are removed. This replaces the "death terminal" line in `persistent-survival.md` and amends REQ-06 and REQ-07 in the master design spec. Implemented in Phase 3.6. |
| D9 | The first wreck always qualifies. `FirstRunAwayGate` patches a contact that misses the first-run contract (adds one encounter, marks a cargo or engineering room breached) and re-rolls derived seeds if that is not enough, instead of refusing travel. Boarding applies the same patch (`RunSession.FirstRun.cs`), so the player gets the wreck the gate accepted. Ordinary `ShipGenerator.GenerateFromSeed` is unchanged. |

## Phase 0.3 decisions

1. **Manual Study stays, without paid crafting.** A plain 30-second study job on the session. Books read persist through the progression summary. Books reach the player through ordinary containers (Phase 0.3c).
2. **Auxiliary Services are deleted** (0.3d). Home-repair goals and finite caches return in Phase 1.3 as ordinary repair points and loot. The fabricator and bulk cargo transfer are no longer gated on a service feed.
3. **`EnableComponentIntegration` is the one remaining default-off flag**, an exception to the "no default-off flags" guardrail. Activation is scheduled for Phase 5.5. Folding it in earlier would orphan existing saves and force a second save-format rewrite in Phase 3.6.
4. **The reviewed first-away profile is deleted** (0.3e) once D9 is satisfied generically. A saved world that carries a `starting_home_anchor` is refused, as it already was when the profile was off.
5. **Item and loot data is now Unity-owned.** `tools/sync-godot-data.ps1` mirrors the Godot repo and deletes files Godot lacks, so a future sync would remove Unity-only data (for example the book items and `items/book_loot_overlay.json` from 0.3c). `item_definitions.json` already drifted from Godot. `loot_tables.json` is still compared with the Godot fixtures by `LootParityTests`, so book drops use their own source, `items/book_loot_overlay.json`, applied by `LootRoller.LoadTablesWithOverlays()`, rather than editing it.
6. **Paid crafting is deleted** (0.3f), with the `EnablePaidCrafting` and `EnableManualStudy` flags and the schema 3/4/5 domains. Saves made with component integration and paid crafting both on (only tests created them) now fail to load as `corrupt_generation` and drop out of the slot list. Orphaned `.paid-craft-generations` folders are harmless. The save payload, commit and pointer versions were not bumped.
7. **Books do not teach recipes yet.** Ordinary crafting has no knowledge gate (`CanCraft(..., knowledge: null)`), so every recipe is already craftable. Gating recipes by books is a later-phase feature.

## Phase 0.4 outcome (dead code)

The master plan's 0.4 list came from file headers, written before 0.3. Each item was checked against the code before deletion.

**Deleted:**
- Work kernel: `WorkEligibility`, `WorkTransactionState`, `IWorkCommitPort` and `WorkKernelTests` (0.4a).
- Infra ledgers: `AutomatedPlaytestRubric`, `BalanceLedger`, `ReleaseReadinessLedger`, `ProductAuditReport`, `IntegrationMatrix`, and the matrix half of `DependencyValidator` (which keeps `VerifyCatalogSources`) (0.4b).
- First-generation graph pipeline: `RoomGraphGenerator`, `StructuralPlacer`, `RoomGraph`, plus unused `LayoutMutator`, `ShipGenerator.GenerateLayout` and `LifeBoatBuilder.BuildGraph` members and their Godot stage-parity fixtures (0.4c).
- Rust worldgen seam: `FrozenDerelictLayoutSource`, `IDerelictLayoutSource`, the `ShipGenerator` worldgen path and the 5.5 MB `StreamingAssets/data/worldgen-fixtures` dump, which shipped in player builds. It stays in git history (0.4d).

**Not deleted, despite the master plan:**
- `PinnedAdmissionResourceScope`, `AuxiliaryWorkRuntime*`, `AuxReplay*` and `AuxiliaryEvidence*` do not exist on `main-next`; they live only on the archive branch.
- `DomainTransactionCoordinator`, `ComponentTransferService`, `ItemInstanceState` and `SaveCommitCoordinator` are labelled "Unused" but component integration reaches them. The headers were corrected.
- `ShipLayoutGenerator`, `TemplateSelector`, `RoomAssigner`, `TopologyTemplate`, `CellLayoutEngine` and the overlay/wreck parts of `LayoutMutator` are the live path for every first away wreck and every size-0 wreck.
- `TuningCatalog` is unused but kept as the loader for Phase 1.3's `balance/survival.json`.

**Later candidates (test-only, not in the master plan):** `StartSceneBuilder`, `SeedDeterminismContract`.

**Formal-build validator retired.** `docs/design/formal-build-2026-10-02/validate_artifacts.py` fails on any deleted file in its preservation baseline, and nothing in CI or `tools/mac` runs it. The packet is a historical record; do not recapture its baseline.

**Size:** about 3,500 lines of product code and tests plus the 258k-line fixture dump, not the master plan's 20k-line estimate.

## Known issues, deferred

- **`RepairPoint.TryStart`** (`Core/Session/Interactables/RepairPoint.cs:140-146`) returns `true` on `insufficient_skill`, and `repair_point` is checked before `breach_seal_point` (`InteractionRegistry.cs`). A breach seal on the same spot as a skill-gated repair point therefore never starts. For the Phase 2 interaction catalog.
- **Scanner "Return home"** is dormant. Only the removed reviewed first-away profile enabled it, so `RunSession.HomeNavigationAvailable` is `false`. The UI contract (`IHomeReturnScannerHost`) is kept for a later phase to re-enable.
- **`InventoryPanel.BindBulkTransferGate`** has no remaining caller that passes a gate; it is bound to `null`.
- **`earned-entry-home-v1`** diagnostic data is kept for `FiniteLootTests` and `FiniteLootGenerationTests`.
- **Finite loot on component Continue:** component-mode Continue does not restore finite loot, and `PartialKitSurvivesCompleteGenerationFreshContinue…` is `[Ignore]`d. Fix before component integration is activated in Phase 5.5.
- **Standalone-player PlayMode:** `tools/mac/test.sh playmode` in the default standalone-player mode loses the player after a forced recompile and produces no result XML. Use `--playmode-target editor` until it is fixed.
- **Study job is not saved:** an in-progress study job is dropped on save and load; books already read persist.
- **Study and crafting:** ordinary crafting is not blocked while a study runs.

## OPEN

- **OPEN-1, book balance:** book XP (200 common, 250 uncommon, 350+ rare) and the loot weights and placement in `book_loot_overlay.json` are placeholders for the product owner to set.
- **OPEN-2, PlayMode failure messages:** the two long-route PlayMode failures now report "player movement stalled…" and "work completed without teleporting…" instead of health 0. Confirm in the standalone player whether they are still the long-route deaths Phase 1.6 fixes.

## Guardrails

Every PR changes something reachable from Title → New Run. No new default-off flags.
