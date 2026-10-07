# Station-tier correction and anchor qualification evidence - 2026-10-02

The spatial station-tier correction is implemented, independently reviewed and committed locally as **15b09d778763c5725c16d56df3244a0ab317d0c0**. Listing, immediate precheck and model start now use the existing station's actual effective tier. This completes the admitted propagation fix; F07, earned upgrades and broader crafting persistence remain incomplete.

The separate empty-container anchor probe supplies bounded physical evidence for a real maintenance cell and normal initial access gates. It does **not** approve a final production kit placement or prove an earned/native-input walk. No kit, books, service jobs, inventory quantities or production source rows were added.

Baseline: a52a347b30c406e5beedd120f0ed7d46b49a5e97, after the [three bounded foundation corrections](foundation-bounded-2026-10-02.md). Work remains on local branch codex/foundation-bounded-2026-10-02 in the existing isolated checkout. The engineer owned nine source/test/meta paths, a separate lane owned only scratch diagnostics, and the coordinator alone ran tests, Unity and git. The concurrent author's audit/design/planning registers remained read-only and untracked. No remote publication or original-checkout/save manipulation occurred.

The [deep audit](../audit-deep/README.md) provides the baseline tier discrepancy. [Current handoff](../design/formal-build-2026-10-02/implementer-handoff.md), [contracts](../design/formal-build-2026-10-02/contracts.md) and [F02 amendment](../design/formal-build-2026-10-02/amendments/f02-sources.md) remain separately authored proposals. The [engineering report](../../artifacts/station-tier-2026-10-02/engineering-report.md), [actual final diff package](../../artifacts/station-tier-2026-10-02/tier-final-review-package.md), [fresh review](../../artifacts/station-tier-2026-10-02/review.md) and [verification summary](station-tier-2026-10-02-verification.json) retain scope, exact source/build fingerprints and execution evidence.

## Implemented behavior

CraftingStation.ListRecipeEntries and its explicit start precheck pass existing StationState.EffectiveTier to the existing recipe evaluator. RunSession.ListStationRecipeEntries delegates to the actual valid spatial station. Confirmation preserves specific current tier, skill, missing-input and output-capacity rejection reasons; fallback CraftingStationHost uses the same effective tier. Busy status remains visible and start rechecks current gates before the unchanged, single model consumption path. Listing does not create/upgrade a station or debit inputs.

Fixtures use actual craft_sensor_module (tier1) and craft_thruster_nozzle (tier2), with diagnostic skill, inventory and model-tier provisioning. They cover actual station/session/UI listing and confirmation, tier/skill/material/output changes after preview, below-tier rejection, current visible failure reasons, duplicate starts and exactly one input debit. They do not establish earned inputs, training, upgrades or book acquisition.

The live catalog registration now records the actual forwarded tier. Its regression requires no omission diagnostic at model tier 2, then deliberately substitutes precheck tier 0 and retains both negative assertions. The ordinary catalog remains invalid for missing content. The catalog test output accepts a phase-specific SYNAPTIC_CATALOG_EVIDENCE_DIR so later runs preserve previous foundation artifacts; default output routing is unchanged.

No CraftingState consumption implementation, recipe, tier assignment, cost, skill requirement, knowledge/book policy, survival rate, class stat, supported New Run setting, death succession, offline policy, topology, ecology, save schema or migration was changed. Existing saved station tiers are read as before; no new persisted fields or compatibility migration is needed.

## Executed verification

All phases have distinct local logs, TRX/XML, source inventories and compiled-assembly hashes. Full failure assertions remain in their original files under artifacts/station-tier-2026-10-02. Final scoped C# bytes match every applicable final Core/Edit/Play inventory; reviewed metadata is separately hashed. Final Edit discovery confirms all 18 new Core and seven new UI cases passed.

| Phase | Discovered | Passed | Failed | Skipped | Meaning |
| --- | ---: | ---: | ---: | ---: | --- |
| tier-red | 18 | 0 | 18 | 0 | Real station/session omission and masked-reason regressions |
| tier-ui-red | 7 | 1 | 6 | 0 | Real picker mismatch; below-tier control passed |
| tier-green | 94 | 94 | 0 | 0 | New Core cases plus crafting, catalog, field, interactable and station-placement coverage |
| tier-ui-green | - | - | - | - | Exit 1 before discovery/XML: Bee response-file writes denied |
| tier-ui-green-2 | 16 | 14 | 2 | 0 | Both closed-panel duplicate-confirm test expectations reproduced |
| tier-ui-green-3 | 16 | 16 | 0 | 0 | Corrected oracle plus nine existing inspection cases |
| tier-core-final | 937 | 937 | 0 | 0 | Full Core aggregate on final product source |
| tier-edit-final | 1246 | 1229 | 0 | 17 | Full Unity Edit aggregate on final product source |
| anchor-probe-1 | 1 | 0 | 1 | 0 | Original bounded deck-marker sample failed; lower-door evidence retained |
| anchor-probe-2 | 1 | 1 | 0 | 0 | Revised standing-approach diagnostic passed its explicit bounded invariants |
| tier-play-final | 14 | 14 | 0 | 0 | Final GPU startup/Continue/results/work regression selection after temporary fixture removal |

The fresh reviewer found an initially unreachable UI test oracle error: successful ConfirmSelection closes the panel and clears station kind; a second direct call returns the existing bad_args before the session busy guard. Unchanged source reproduced both busy-expected/bad_args-actual failures. Only that expected reason and explanatory comment changed; ok=false and exact remaining input quantities remain asserted and passed. Valid session/station repeated starts still assert busy and one model debit. No product closed-panel policy was changed to satisfy the fixture.

The 17 Edit skips are the same vendor environment exclusions: 15 missing built Critter library cases, one absent connector fixture and one export probe without its FBX/catalog environment paths. Full PlayMode, standalone player build, native OS input, earned class/first-ship progression, book/paid-input persistence, new-generation/schema fault matrices and long-term survival were not run.

Retained UI limit: a cached blocked row requires Refresh before reflecting newly improved gates. Ready-row confirmation and direct session start revalidate current gates. This correction does not claim automatic unblocking of the entire panel. The ordinary tier 0 catalog report also retains two tier_not_forwarded rows because the validator compares precheck tier to the recipe minimum; those rows describe an unmet current tier gate and are not proof that this corrected caller still omits the model tier. Diagnostic classification/source-path refinement remains separate work. Missing_definition (45) and missing_source (116) diagnostic rows remain; these are rows rather than unique-item or whole-world counts.

## Bee failure and incidental settings

The pre-discovery error was Error opening for writing the file under this isolated project's Library/Bee/artifacts/rsp, Permission denied. The [exact error/process/ACL record](../../artifacts/station-tier-2026-10-02/bee-diagnosis.json) and original failed log are retained. No engineering/anchor lane launched Unity. After the failed Editor exited, no test Editor/Bee remained; sampled failed response files were not read-only and opened read/write without modifying bytes. The unrelated PID 29812 Unity MCP process for F:/synaptic-sea/SynapticSea was preserved.

No permissions were broadened or denied action bypassed. The coordinator verified absolute workspace bounds and absence of reparse points, then non-destructively preserved only this task's 204 derived response files at Library/Bee/artifacts/rsp-preserved-tier-20261002T0748 and allowed Unity to rebuild its response directory. The [preservation manifest](../../artifacts/station-tier-2026-10-02/cache-preservation.json) contains original file hashes. Exact root cause remains undetermined; the failed attempt is never counted as a test pass. Subsequent runs succeeded and no current permission/connection blocker remains.

After the final Editor exit, the coordinator verified that reversing only the added AppUI config-object line and Standalone define change exactly reproduced the pre-run baseline bytes, then restored those two files. [Exact diff](../../artifacts/station-tier-2026-10-02/unity-generated-settings.diff) and [restoration hashes](../../artifacts/station-tier-2026-10-02/settings-restoration.json) are retained. No other concurrent edit was overwritten. No generated settings or temporary diagnostic source/meta is committed.

## Anchor: measured identity, interpretation and limits

The [anchor report](../../artifacts/station-tier-2026-10-02/anchor/anchor-report.md) retains original/revised fixture proposals, both executions, source hashes and final blockers. The actual supported default Title New Run loaded res://data/procgen/golden/coherent_ship_001/layout.json and its gameplay_slice.json. This verifies the loader/source for this run; it does not invent a new placement ID.

| Identity | Qualified observation |
| --- | --- |
| Room/cell | maintenance_01, floor_cell_d1_x5_z1; cell_key 1\|5\|1; runtime key floor/1\|5\|1 |
| Authored floor ship-local | [20,4,4], floor collider top approximately 4.125m |
| Earlier tested proposal | [19.2,4.4,4], interpreted explicitly as interaction root in the Godot ship-local frame |
| Current descriptor projection | approach_cell[x,z,deck]=[5,1,1] produces [20,4.12,4]; proposed offsets are dropped |
| Actual candidate standing | Godot world [20.4,4.145,4], same deck, clear capsule/floor/range/LOS |
| Actual lower deck approach | [16.00639,0.145,0.409744], horizontal 0.4097942m from marker, inside ordinary InReach |
| Focused renderer bounds | Unity center[-19.2,4.85,4], size[0.93,1.04,0.78] |

The tested coordinate comes from the earlier F02 snapshot SHA256 11281e0cc720708bc7b057e549b8173b8267ee039e7a49d24f29af00599b78f8. The current placement-pending amendment SHA256 5abe1419969c9d1c56b0bbaff82db65683a6d65494dbc734392317b51981ac76 removes those candidate coordinates and awaits qualification. Neither draft is edited by this task.

Probe1 passed complete lower-deck segments and normal dispatch for doors 0\|v\|0\|1 and 0\|v\|0\|3, then failed the original 0.55m marker-centered NavMesh sample. Revision2 kept that failure and all gate guards, searched explicit standing requests with 0.3m local samples inside the source-backed 1.2m actual horizontal envelope, and required same-deck capsule/home-floor clearance, complete paths without crossing closed portals and production InReach. It rechecked the exact selected point before each labelled fixture teleport. This is a corrected diagnostic approach, not a changed doorway, station, obstacle or reach policy.

Probe2 executed the actual deck-transition focus/dispatch, opened upper door 1\|h\|0\|5 normally, measured a complete maintenance segment, and required real sensor overlap, LOS, normal Search focus and empty loot dispatch. All six home doors began closed/unlocked; only the three route doors changed. Powered route blockers, inventory, full progression/XP/books and objective sequence retained their initial values. Hazards and time remained active. The inserted empty target used the actual production loot_crate mesh/trigger and fixture-only reflection; no production API/content/source was added.

The [retained game-camera image](../../artifacts/station-tier-2026-10-02/anchor-probe-2/synthetic-empty-candidate-game-camera.png) was independently inspected: the brown candidate is visible next to the cyan player and doorway. It is a camera render, without HUD or native-input proof. Measured candidate bounds overlap no Structure/ZoneBlocker/Portal collider or recorded initial door footprint; the live neighboring-renderer scan emitted no candidate AABB overlap. Existing station/component placement was preserved by inserting the candidate only after boot.

**Production placement remains unqualified.** BuildLootContainerSpecs currently cannot retain the required offsets. Root-versus-box semantics and grounded Y must be settled: the nominal unfocused crate at root Y=4.4 has its visual bottom Y=4.4, about 0.275m above the source floor top Y=4.125. The measured focused bounds include 1.15 focus scaling and must not be substituted for that nominal calculation. An authored row can also alter component/station occupancy, which this after-boot insertion does not qualify.

Deliberate fixture teleports followed measured same-deck paths; ordinary deck transfer also teleports. These facts certify NavMesh/collider/focus/dispatch invariants, not continuous walking, natural gameplay, every class's earned acquisition, long-term safety, contents or a finished game. The final authored descriptor/offset/root/occupancy contract and a fresh authored-row scene verification are exact dependencies for a production kit anchor.

## Delivery

Product commit: **15b09d778763c5725c16d56df3244a0ab317d0c0**. This separate implementation evidence and machine-readable summary are committed after product verification. The prior catalog/work/storage corrections and their local IDs remain in the linked foundation note. Raw artifacts and preserved derived cache stay local/ignored; author-generated planning registers are never staged. No remote push, PR, merge, deployment, hard reset, blanket staging, user-save manipulation or original-assets modification occurred.
