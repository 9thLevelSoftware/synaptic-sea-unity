# Starting-home guarantee sweep (Phase 1.9)

Measurement of `StartSceneBuilder.BuildHomeStart(..., guarantee: new StartingHomeGuarantee.Spec())` over seeds 1..200 (`breach_field` / `standard`, medium, Pristine layout, exterior-edge dock, clock scale 60x), from `StartingHomeGuaranteeSweepTests.EverySeedGetsAGuaranteedSafeHome` (`SYNAPTICSEA_SEED_SWEEP` sets the count, `SYNAPTICSEA_GUARANTEE_REPORT` writes the table). Generated homes only; the golden hub keeps its authored caches and is not changed.

## What every accepted home is checked for

For each of the 200 seeds the test asserts, after an independent re-derivation (`StartingHomeGuarantee.Validate`):

- the three containers `home_repair_cache_a`, `home_repair_cache_b` and `home_survival_stores` exist exactly once, after the existing containers, in three different non-start, non-dock rooms, each on a real loot slot, and the slice document and both JSON text mirrors agree;
- every part, spare and tool the opening damage needs, plus one `thruster_nozzle` and one `fuel_line`, plus the food and water, is present;
- no encounters, arc/fire/breach zones, blocked links, module damage or LOCKED/BREACH edges remain, and every room is standard;
- each cache room is standing-reachable from the start room and the dock room is reachable from it;
- life support, scanners and gravity open healthy;
- **a survivor of repair skill 0, 1, 2, 3 and 4 who uses only the home's own parts (consuming one part per repair, as the game does) can repair the lifeboat and pass the real `AssemblyMobility.Evaluate` check with the whole kit carried as cargo**;
- over 30 game hours at 60x the survivor keeps hunger at or above 25 and thirst at or above 40, both when the stores work fully and when only 60% of each item's effect counts (stale food).

## Results

| Measure | Result |
| --- | --- |
| Seeds swept | 200 |
| `BuildHomeStart` with the guarantee succeeds within 8 attempts | 200 / 200 (100.0%) |
| Attempts used (successes) | 1 x165; 2 x28; 3 x7 |
| Rejection reasons (all attempts) | no_free_slot x39; dock room airlock_01 has no exterior-facing edge for the life boat x3 |
| Rooms per home, min / median / max | 6 / 9 / 13 |
| Broken power/navigation/propulsion parts at New Run, min / median / max | 1 / 2 / 3 |
| Extra breaks beyond nav_linkage (0 / 1 / 2) | 1 x82; 0 x62; 2 x56 |
| Repair kit weight kg, min / median / max | 14.0 / 18.0 / 23.0 |
| Survival stores weight kg, min / median / max | 8.9 / 8.9 / 8.9 |
| Rations, min / median / max | 7 / 7 / 7 |
| Water, min / median / max | 19 / 19 / 19 |
| Lifeboat capacity margin kg after skill-0 repairs and the whole kit aboard, min / median / max | 668 / 873 / 877 |
| Guarantee containers per home | 3 (`home_repair_cache_a`, `home_repair_cache_b`, `home_survival_stores`), each in a different non-start, non-dock room |
| Homes whose skill-0 survivor cannot leave using only the home's parts | 0 / 200 |
| Homes whose skill-1 survivor cannot leave using only the home's parts | 0 / 200 |
| Homes whose skill-2 survivor cannot leave using only the home's parts | 0 / 200 |
| Homes whose skill-3 survivor cannot leave using only the home's parts | 0 / 200 |
| Homes whose skill-4 survivor cannot leave using only the home's parts | 0 / 200 |
| Worst hunger / thirst over 30 game hours, all stores used | 79.9 / 85.0 |
| Worst hunger / thirst, only 60% of each item's effect (stale food) | 37.9 / 41.9 |
| Problems | none |

## Reading the numbers

- **Success is 100%, but not on the first try everywhere.** 35 of 200 seeds needed a second or third seed. The rejections are `no_free_slot` (the home does not have three eligible rooms with a free slot) and the dock planner finding no exterior edge on the airlock room (3 attempts). Reseeding is cheap and the requested seed still drives the Synaptic Sea world (Phase 1.8).
- **The lifeboat is much less broken.** 1 to 3 power/navigation/propulsion parts are broken at New Run (median 2), against a median of 3 and up to 6 on the Damaged hubs measured in Phase 1.7b; life support, scanners and gravity are never broken (they were 99% of homes before).
- **Margin.** The skill-0 survivor leaves with at least 668 kg of capacity to spare with the entire kit aboard (the lifeboat supports about 5.5 t).
- **Why margin 1.8 for food and water.** At 1.7, a pessimistic 60%-effect run dipped just under the thirst floor (38.9 against 40; 1.5 was not run, it is lower still). 1.8 gives 7 rations and 19 water (8.9 kg) for 30 game hours. The simulation's eating policy is simple (eat and drink when a full item fits), so treat it as a floor check, not a prediction of play.
- **Weight.** The two repair caches hold 14 to 23 kg between them (each well under the 30 kg cap, 60% of the 50 kg bag) and the stores 8.9 kg.

## What this does not prove

- No rendered scene: placement uses the layout's interior slot data, and the lifeboat fit is the Phase 1.7c geometric model (verified against the real docking transform, not against rendered colliders).
- No 30-minute idle run on a generated home and no real-route PlayMode proof; those are PR F.
- The typed onboarding objectives do not exist on generated homes yet (PR E), so nothing is repaired for free; the weakest-survivor check above deliberately assumes every repair is done by hand.
