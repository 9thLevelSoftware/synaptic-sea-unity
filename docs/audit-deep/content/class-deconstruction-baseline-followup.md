# Class, exchange and baseline followup

Current `840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48`; published baseline `8dcc95c10ab5e08658546319f51f49b4abd256fc`. Fresh engine-free diagnostics use current Core and actual catalogs. These are model diagnostics, not a natural onboarding walk.

| Class | Title fresh | Initial repair | After 2 onboarding events level / XP | Extra effective XP for repair2 | Cache repairs before objectives -> final level / XP |
|---|---|---:|---|---:|---|
| engineer | available | 3 | 3 / 360 | 0 | 5 / 472 |
| mechanic | available | 4 | 4 / 360 | 0 | 6 / 272 |
| medic | available | 1 | 1 / 84 | 116 | 1 / 154 |
| pilot | available | 1 | 1 / 120 | 80 | 2 / 20 |
| scientist | available | 2 | 2 / 144 | 0 | 3 / 324 |
| cook | available | 0 | 0 / 96 | 204 | 0 / 96 |
| security | available | 1 | 1 / 108 | 92 | 1 / 198 |
| communications | available | 0 | 1 / 8 | 192 | 1 / 8 |
| salvage_captain | locked | 1 | 1 / 132 | 68 | 2 / 42 |
| field_medic | locked | 0 | 0 / 96 | 204 | 0 / 96 |
| signal_specialist | locked | 0 | 1 / 20 | 180 | 1 / 20 |

These two objective events correspond to restore_systems and stabilize_reactor. They each grant base repairXP120. Cross-category training halves that amount before the class technical multiplier. Level0->1 costs100 and1->2 costs200. RepairPoint completion additionally grants repairXP25 directly plus repair_subcomponent base50 through the bus.

Current local cache provisions exactly20 units in10 stacks, after physical acquisition. With the condition1/seed17 manager and opening propulsion override, battery_cells and star_charts are the only repair1 targets compatible with these cache parts. Repairing both BEFORE onboarding enables pilot and salvage_captain to reach repair2; security ends2XP short and medic46XP short. Completing restore_systems first force-repairs battery_cells and removes that training opportunity. Cook/field_medic remain repair0/96 after both objective events, cannot start any of the18 repairs (minimum1), and DO have the live cargo breach seal repair0 XP producer, discovered in the supplemental followup. The earlier progression trap candidate is superseded by predeparture-repair-producers.md; no deadlock is established. Communication/signal can start repair1 after objectives but remain below repair2 with these finite opportunities. Hazard-induced future damage, probabilistic additional loot and owned hub bonuses are separate conditional sources, not granted here.

Title unlocks: salvage_captain requires scavenge_container log at EndRun; field_medic requires perform_surgery (currently source-less gauze dependency); signal_specialist requires decode_signal voice log. All11 catalog objects can be configured directly; that does not make the3 unlockable classes fresh-title choices.

All62 recipes use AND ingredients; none use alternative OR inputs. Fresh exact-input checks succeeded at required tier; removing1 unit of any listed ingredient rejected every recipe. Effective installed tier2 is derived correctly, but spatial TryCraftRecipe still returnsfalse while direct CanCraft(actualTier2) returns true. Explicit unknown knowledge rejects; null knowledge accepts.

## Exact deconstruction

| Selection | Required input | Single chosen output | Paired craft/deconstruct net |
|---|---|---|---|
| deconstruct_scrap | {'scrap_metal': 1} | {'item_id': 'ferrous_shard', 'quantity': 2} | [] |
| deconstruct_plating | {'plating': 1} | {'item_id': 'scrap_metal', 'quantity': 3} | [{'scrap_metal': 1, 'adhesive_paste': -1}] |
| deconstruct_power_cell | {'power_cell': 1} | {'item_id': 'wiring_bundle', 'quantity': 2} | [{'scrap_metal': -1, 'reactive_gel': -1}] |
| deconstruct_sensor | {'sensor_module': 1} | {'item_id': 'circuit_board', 'quantity': 1} | [{'sensor_array': -1, 'optical_lens': -1}] |
| deconstruct_thruster | {'thruster_nozzle': 1} | {'item_id': 'titanium_ingot', 'quantity': 1} | [{'titanium_ingot': -1, 'ceramic_plate': -2, 'coolant_fluid': -1}] |
| deconstruct_welder | {'welder': 1} | {'item_id': 'scrap_metal', 'quantity': 2} | [{'scrap_metal': -1, 'power_cell': -1, 'circuit_board': -1}] |
| deconstruct_plasma_cutter | {'plasma_cutter': 1} | {'item_id': 'titanium_ingot', 'quantity': 1} | [{'titanium_ingot': -1, 'fusion_igniter': -1, 'power_cell': -1, 'circuit_board': -2}] |

All7 execute immediately through salvage station with no player skill or power/time gate; authored batch_size is not an additional multiplier. They are single-output recipes. The4 junk definitions are the separate multi-yield path. Every paired roundtrip spends at least one input; no self-sustaining duplication was demonstrated. Plating gains1 scrap at cost1 adhesive; power-cell roundtrip returns its2 wiring but spends1 scrap+1 gel. Other roundtrips also spend reagents; sensor/nozzle/cutter crafting is already unavailable through missing inputs and/or tier gate. Coolant output2 from input1 is seeded reagent conversion, requiring2 water+1 gel each time; current seed coolant source is absent. Simple graph cycles cannot prove bootstrap or duplication.

Cancellation consumes inputs without refund, and the cancel API has no live UI/session caller. Truthful partial craft+inventory JSON restore yielded exactly1 cooked_meal and a second finish returned empty. Queue auto-start free consumption remains a disconnected API path.

## Published baseline distinction

Progression models, crafting models, deconstruction code and class definitions are identical Git blobs to8dcc. The recipe catalog was60 on baseline; the only2 local additions are lockpick_set and hack_chip recipes. The fixed20-unit home starter supply is LOCAL: baseline used random repair_parts_starter rolls. Reclaimed-home services/boarding paths and home-work continuations are local-changed files and must not be described as published behavior. Full per-evidence-file Git blob comparison is in followup-class-deconstruction-baseline.json.

Fresh-model-experiment-manifest.json records source hashes, current compiled DLL hash, harness/output hashes, SDK/command, seed/classes/settings, expected/actual and proof boundary. No Unity or user saves were touched.
