# Generated-home viability sweep (Phase 1.7b)

Measurement only. No production behavior changed. Question: can `StartSceneBuilder.BuildHomeStart` (Core/Procgen/StartSceneBuilder.cs) produce a home that could host a New Run, and what does it take to make that home safe, repairable and dock-ready? Test: `Tests/EditMode/Procgen/HomeGenerationSweepTests.cs`. Rerun it with `SYNAPTICSEA_SEED_SWEEP=200 SYNAPTICSEA_SWEEP_REPORT=<path> tools/mac/test.sh dotnet --filter HomeGenerationSweepTests`; it takes about 45 seconds.

## Method

- Seeds 1..200, biome `breach_field`, difficulty `standard`, size Medium, through `BuildHomeStart(seed, biome, difficulty, condition: ...)` with today's `Damaged` condition and with `Pristine`. `BuildHomeStart` already takes the condition as a parameter, so no production seam was needed.
- Every home is measured from its layout and gameplay slice: rooms, floor cells, free interior loot slots (the `FirstRunAwayGate.AddFirstWreckStores` rules, start room excluded), dock room versus airlock fallback, hazards of every kind, start-room-to-dock reachability (the enclosure rules the gate uses, and the stricter standing rules), and the opening ship-systems damage (`ShipSystemsManager.Configure` plus the `ApplyLifeboatOpeningDamage` rule).
- **Lifeboat fit** is a best-effort geometric estimate. It follows the docking math: `DockPorts.ForLifeboat` puts the boat's port half a cell west of its airlock centre, and `DockingManager.ComputeMobileTransform` lands that port on the home port (the home dock/airlock room's floor centre, facing +X). The boat's airlock centre therefore sits half a cell east of the home port, with its engine bay one cell west of that and its cockpit one cell east (3 cells in a row, 12 m span). The check counts home cells of any room other than the dock/airlock room that the span overlaps. The game does not collision-check the dock, so overlap is a quality measure, not a hard failure. The golden hub itself overlaps one other-room cell and is the only home known to dock correctly, so the calibrated check is "no worse than the golden hub".
- **Confidence in the lifeboat-fit numbers: medium.** The model follows the code, but it has not been checked against rendered geometry or the in-game scene, and the game never collision-checks the dock. The constant overlap of 2 across 12 of 14 templates suggests the fixed +X anchor dominates the result. Treat the percentages as indicative.
- Determinism: the first 10 seeds of both conditions generate byte-identical documents twice (asserted by the test).
- The bar used for the go/no-go: success within 8 attempts, at least 3 non-connective rooms, at least 3 free interior slots outside the start room, start room reaches the dock/airlock room by standing rules, and the lifeboat fits.

## Sweep results (200 seeds, biome `breach_field`, difficulty `standard`, size Medium)

### Damaged layout condition (today's `StartSceneBuilder.HOME_CONDITION`)

| Measure | Result |
| --- | --- |
| Seeds swept | 200 |
| `BuildHomeStart` succeeds within 8 attempts | 200 / 200 (100.0%) |
| Succeeds on the first attempt | 200 / 200 (100.0%) |
| Attempts used (successes) | 1 x200 |
| Rejection reasons (all attempts) | none |
| Templates chosen | bifurcated x23; derelict_b x18; compact x17; radial x16; ring x16; dispersed x15; hive x14; stacked x14; stacked_v2 x14; derelict_a x13; double_spine x12; vault x11; hangar_wing x10; spine x7 |
| Rooms, min / median / max | 6 / 9 / 13 |
| Non-connective rooms, min / median / max | 2 / 5 / 7 |
| Floor cells, min / median / max | 24 / 43 / 97 |
| Free interior loot slots outside the start room, min / median / max | 9 / 23 / 68 |
| Rooms with a free slot, min / median / max | 2 / 7 / 11 |
| Dedicated dock room (else airlock / boarding fallback) | 55 / 200 (27.5%) |
| Anchor source | boarding x145; dock x55 |
| Start room reaches the dock/airlock room (enclosure rules, the gate's model) | 200 / 200 (100.0%) |
| Start room reaches the dock/airlock room (standing rules: OPEN/DOOR/HATCH only) | 200 / 200 (100.0%) |
| Lifeboat footprint overlaps only the dock/airlock room | 0 / 200 (0.0%) |
| Lifeboat overlap with other rooms' cells, min / median / max | 1 / 2 / 2 |
| Homes with any breach zone | 0 / 200 (0.0%); zones min / median / max 0 / 0 / 0 |
| Homes with authored encounters | 192 / 200 (96.0%); markers min / median / max 0 / 2 / 9 |
| Homes with an arc zone | 68 / 200 (34.0%) |
| Homes with blocked links / module damage / LOCKED or BREACH edges | 200 / 200 / 200 of 200 |
| Homes with a room variant other than standard | 200 / 200 (100.0%) |
| Homes with no hazard of any kind (breach, encounter, arc, blocked link, module damage, LOCKED/BREACH edge, non-standard room) | 0 / 200 |
| Broken power / navigation / propulsion subs at New Run (after the opening damage), min / median / max | 1 / 3 / 6 |
| Homes that start with a broken life_support / scanners / gravity sub | 198 / 200 (99.0%) |
| Lifeboat overlap no worse than the golden hub's (1 other-room cell) | 14 / 200 (7.0%) |
| Start room (the airlock/dock) has a non-standard variant | 141 / 200 (70.5%) |
| Homes with a hazardous room variant (breached, flooded, collapsed, burned_out, biomatter_crusted); rooms per home min / median / max | 184 / 200 (92.0%); 0 / 3 / 7 |
| Homes with an encounter marker in the start room | 0 / 200 (0.0%) |
| Room variants (all rooms of all homes) | standard x362; collapsed x164; flooded x156; breached x119; junction x69; narrow x64; biomatter_crusted x57; command x54; wide x53; observation x44; bio_seal x39; standard_life_support x38; cargo_lock x37; dark_bridge x37; hold x34; refrigerated x34; maintenance_hatch x29; cargo x28; officer x28; burned_out x27; derelict_bunks x27; empty_hold x26; bunks x25; unstable x25; service x23; lifeboat x22; secure x21; locked x19; sealed x18; surgery x18; central x17; contaminated x16; triage x16; long_table x12; climate_controlled x11; general x10; reactor x7; mess x6; standard_security x6; tool_storage x6; life_support x5; propulsion x5; secondary x5; small_craft x4; primary x1 |
| Start room is the dock/airlock room itself | 200 / 200 (100.0%) |
| Module-damage entries, min / median / max | 25 / 52 / 100 |
| Homes with >= 3 non-connective rooms | 193 / 200 (96.5%) |
| Meets the bar if the lifeboat-fit check is ignored | 193 / 200 (96.5%) |
| Meets the bar with the lifeboat check calibrated to the golden hub | 14 / 200 (7.0%) |
| **Meets the bar** (success, >= 3 non-connective rooms, >= 3 free slots outside the start room, start reaches the dock by standing rules, lifeboat fits) | **0 / 200 (0.0%)** |

By template (lifeboat overlap with other rooms' cells; calibrated pass = no worse than the golden hub's 1):

| Template | Homes | Dedicated dock room | Overlap min / median / max | Calibrated pass |
| --- | --- | --- | --- | --- |
| bifurcated | 23 | 0 | 1 / 2 / 2 | 5 / 23 |
| compact | 17 | 0 | 2 / 2 / 2 | 0 / 17 |
| derelict_a | 13 | 13 | 2 / 2 / 2 | 0 / 13 |
| derelict_b | 18 | 18 | 2 / 2 / 2 | 0 / 18 |
| dispersed | 15 | 0 | 2 / 2 / 2 | 0 / 15 |
| double_spine | 12 | 0 | 2 / 2 / 2 | 0 / 12 |
| hangar_wing | 10 | 10 | 2 / 2 / 2 | 0 / 10 |
| hive | 14 | 14 | 2 / 2 / 2 | 0 / 14 |
| radial | 16 | 0 | 2 / 2 / 2 | 0 / 16 |
| ring | 16 | 0 | 2 / 2 / 2 | 0 / 16 |
| spine | 7 | 0 | 1 / 1 / 2 | 5 / 7 |
| stacked | 14 | 0 | 1 / 2 / 2 | 4 / 14 |
| stacked_v2 | 14 | 0 | 2 / 2 / 2 | 0 / 14 |
| vault | 11 | 0 | 2 / 2 / 2 | 0 / 11 |

By anchor source (calibrated pass): dock room 0 / 55; airlock/boarding fallback 14 / 145

Seeds with no viable home in 8 attempts: none

Valid homes with fewer than 3 non-connective rooms: 7 (seeds: 41 [stacked_v2], 45 [stacked_v2], 48 [stacked_v2], 57 [stacked_v2], 84 [stacked_v2], 119 [stacked_v2], 155 [stacked_v2])

Valid homes that miss the bar: 200 (first seeds: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)

### Pristine layout condition

| Measure | Result |
| --- | --- |
| Seeds swept | 200 |
| `BuildHomeStart` succeeds within 8 attempts | 200 / 200 (100.0%) |
| Succeeds on the first attempt | 200 / 200 (100.0%) |
| Attempts used (successes) | 1 x200 |
| Rejection reasons (all attempts) | none |
| Templates chosen | bifurcated x23; derelict_b x18; compact x17; radial x16; ring x16; dispersed x15; hive x14; stacked x14; stacked_v2 x14; derelict_a x13; double_spine x12; vault x11; hangar_wing x10; spine x7 |
| Rooms, min / median / max | 6 / 9 / 13 |
| Non-connective rooms, min / median / max | 2 / 5 / 7 |
| Floor cells, min / median / max | 24 / 43 / 97 |
| Free interior loot slots outside the start room, min / median / max | 9 / 23 / 68 |
| Rooms with a free slot, min / median / max | 2 / 7 / 11 |
| Dedicated dock room (else airlock / boarding fallback) | 55 / 200 (27.5%) |
| Anchor source | boarding x145; dock x55 |
| Start room reaches the dock/airlock room (enclosure rules, the gate's model) | 200 / 200 (100.0%) |
| Start room reaches the dock/airlock room (standing rules: OPEN/DOOR/HATCH only) | 200 / 200 (100.0%) |
| Lifeboat footprint overlaps only the dock/airlock room | 0 / 200 (0.0%) |
| Lifeboat overlap with other rooms' cells, min / median / max | 1 / 2 / 2 |
| Homes with any breach zone | 0 / 200 (0.0%); zones min / median / max 0 / 0 / 0 |
| Homes with authored encounters | 192 / 200 (96.0%); markers min / median / max 0 / 2 / 9 |
| Homes with an arc zone | 68 / 200 (34.0%) |
| Homes with blocked links / module damage / LOCKED or BREACH edges | 0 / 0 / 0 of 200 |
| Homes with a room variant other than standard | 200 / 200 (100.0%) |
| Homes with no hazard of any kind (breach, encounter, arc, blocked link, module damage, LOCKED/BREACH edge, non-standard room) | 0 / 200 |
| Broken power / navigation / propulsion subs at New Run (after the opening damage), min / median / max | 1 / 1 / 1 |
| Homes that start with a broken life_support / scanners / gravity sub | 0 / 200 (0.0%) |
| Lifeboat overlap no worse than the golden hub's (1 other-room cell) | 14 / 200 (7.0%) |
| Start room (the airlock/dock) has a non-standard variant | 141 / 200 (70.5%) |
| Homes with a hazardous room variant (breached, flooded, collapsed, burned_out, biomatter_crusted); rooms per home min / median / max | 184 / 200 (92.0%); 0 / 3 / 7 |
| Homes with an encounter marker in the start room | 0 / 200 (0.0%) |
| Room variants (all rooms of all homes) | standard x362; collapsed x164; flooded x156; breached x119; junction x69; narrow x64; biomatter_crusted x57; command x54; wide x53; observation x44; bio_seal x39; standard_life_support x38; cargo_lock x37; dark_bridge x37; hold x34; refrigerated x34; maintenance_hatch x29; cargo x28; officer x28; burned_out x27; derelict_bunks x27; empty_hold x26; bunks x25; unstable x25; service x23; lifeboat x22; secure x21; locked x19; sealed x18; surgery x18; central x17; contaminated x16; triage x16; long_table x12; climate_controlled x11; general x10; reactor x7; mess x6; standard_security x6; tool_storage x6; life_support x5; propulsion x5; secondary x5; small_craft x4; primary x1 |
| Start room is the dock/airlock room itself | 200 / 200 (100.0%) |
| Module-damage entries, min / median / max | 0 / 0 / 0 |
| Homes with >= 3 non-connective rooms | 193 / 200 (96.5%) |
| Meets the bar if the lifeboat-fit check is ignored | 193 / 200 (96.5%) |
| Meets the bar with the lifeboat check calibrated to the golden hub | 14 / 200 (7.0%) |
| **Meets the bar** (success, >= 3 non-connective rooms, >= 3 free slots outside the start room, start reaches the dock by standing rules, lifeboat fits) | **0 / 200 (0.0%)** |

By template (lifeboat overlap with other rooms' cells; calibrated pass = no worse than the golden hub's 1):

| Template | Homes | Dedicated dock room | Overlap min / median / max | Calibrated pass |
| --- | --- | --- | --- | --- |
| bifurcated | 23 | 0 | 1 / 2 / 2 | 5 / 23 |
| compact | 17 | 0 | 2 / 2 / 2 | 0 / 17 |
| derelict_a | 13 | 13 | 2 / 2 / 2 | 0 / 13 |
| derelict_b | 18 | 18 | 2 / 2 / 2 | 0 / 18 |
| dispersed | 15 | 0 | 2 / 2 / 2 | 0 / 15 |
| double_spine | 12 | 0 | 2 / 2 / 2 | 0 / 12 |
| hangar_wing | 10 | 10 | 2 / 2 / 2 | 0 / 10 |
| hive | 14 | 14 | 2 / 2 / 2 | 0 / 14 |
| radial | 16 | 0 | 2 / 2 / 2 | 0 / 16 |
| ring | 16 | 0 | 2 / 2 / 2 | 0 / 16 |
| spine | 7 | 0 | 1 / 1 / 2 | 5 / 7 |
| stacked | 14 | 0 | 1 / 2 / 2 | 4 / 14 |
| stacked_v2 | 14 | 0 | 2 / 2 / 2 | 0 / 14 |
| vault | 11 | 0 | 2 / 2 / 2 | 0 / 11 |

By anchor source (calibrated pass): dock room 0 / 55; airlock/boarding fallback 14 / 145

Seeds with no viable home in 8 attempts: none

Valid homes with fewer than 3 non-connective rooms: 7 (seeds: 41 [stacked_v2], 45 [stacked_v2], 48 [stacked_v2], 57 [stacked_v2], 84 [stacked_v2], 119 [stacked_v2], 155 [stacked_v2])

Valid homes that miss the bar: 200 (first seeds: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)

### Baseline: the golden hub (`coherent_ship_001`) through the same checks

| Measure | Golden hub |
| --- | --- |
| Rooms / non-connective rooms / floor cells | 8 / 4 / 26 |
| Free interior loot slots outside the start room | 0 in 0 rooms (its supplies are authored containers) |
| Dedicated dock room | False (start room is the dock/airlock room: True) |
| Start reaches the dock/airlock room (enclosure / standing) | True / True |
| Lifeboat footprint overlaps only the dock/airlock room (strict check) | False (overlap with other rooms' cells: 1) |
| Breach zones / encounters / arc zones / blocked links / module damage / LOCKED or BREACH edges | 0 / 0 / 0 / 1 / 0 / 0 |
| Room variants | standard x8 |

The golden hub is the only home the lifeboat is known to dock to correctly, and it fails the strict lifeboat-fit check (overlap 1). So the strict check is stricter than what the game accepts. The tables above also report a calibrated check: other-room overlap no worse than the golden hub's 1.


## What the numbers say

1. **Generation itself is robust, but the gate filters nothing.** All 200 seeds produce a home on the first attempt in both conditions, with zero rejections. `ValidateHomeStart` never rejected anything, so the reseed loop gives no protection yet. Every quality requirement below has to be added to the gate. Notable failure seeds: none fail to generate. Seven seeds (41, 45, 48, 57, 84, 119, 155; all template `stacked_v2`) have fewer than 3 non-connective rooms.
2. **Pristine removes the wreck damage and nothing else.** Compared with `Damaged`, `Pristine` removes all blocked links, all LOCKED/BREACH edges and all module damage (25 to 100 entries per home under `Damaged`). It also cuts the opening ship-systems damage from a median of 3 broken power/navigation/propulsion subs (up to 6) to exactly 1 (`nav_linkage`). Under `Damaged`, 99% of homes also start with a broken life_support, scanners or gravity sub; under `Pristine`, none do. This confirms the plan to generate the home `Pristine`, and it also delivers the "less broken lifeboat" requirement for free, because the lifeboat mirrors the home's systems.
3. **Pristine does not make the home safe.** Hazards that remain in every condition: non-standard room variants in 100% of homes (92% have at least one hazardous variant: breached, flooded, collapsed, burned_out or biomatter_crusted; median 3 rooms per home), authored encounter markers in 96% (none in the start room), and an arc zone in 34%. 70.5% of start rooms (the airlock/dock) themselves carry a non-standard variant. No home has zero hazards. Making the home "the safest place the player will ever be" needs an explicit strip: set all variants to standard, clear encounters and arc zones. Whether that leaves a structurally valid layout (a collapsed room may carry blocked geometry) is unverified and must be tested.
4. **The start room is always the dock/airlock room.** In 200 of 200 homes the gameplay slice's start room is the airlock (or dock) room, so "start reaches the dock" is trivially true here and the player spawns at the lifeboat. That suits the guarantee flow. The check that matters is reachability to the supply caches.
5. **Only 27.5% of homes have a dedicated dock room.** The rest anchor on the airlock/boarding cell.
6. **Size is fine.** 6 to 13 rooms (median 9), 24 to 97 floor cells, and at least 9 free interior slots outside the start room in every home. 96.5% have at least 3 non-connective rooms; the other 3.5% are all `stacked_v2` and are caught by a minimum-room gate plus a reseed.
7. **Lifeboat fit is the open problem.** By the strict rule (overlap only the dock/airlock room), 0 of 200 pass, but the golden hub also fails it (1 other-room cell), so the calibrated rule is the fair one. By that rule only 14 of 200 homes (7%) are no worse than the golden hub, almost all of them in the `spine`, `bifurcated` and `stacked` templates. The cause is structural: the boat is always placed along +X from the airlock's floor centre, regardless of where the hull's open edge is, so unless the airlock sits at the +X boundary the boat's span overlaps the neighbouring cells. Retrying seeds cannot fix a 7% pass rate: 8 attempts would give about 44% success (`1 - 0.93^8`), and about 31% per attempt is needed for 95% within 8.

## Validator and generator extensions needed

1. **Dock port facing and anchor from the geometry** (required). Derive the port's facing from the airlock/dock room's open (exterior) edge, or pick the airlock on the hull's +X boundary, instead of the hard-coded +X from the floor centre (`DockPorts.ForDerelict`). Then re-run this sweep. Target: at least 95% of seeds meet the calibrated lifeboat-fit rule within 8 attempts.
2. **Replace the estimate with the real transform** in `ValidateHomeStart`: compute the lifeboat's floors under `DockingManager.ComputeMobileTransform` and test them against the home's floors with the same 3.95 m tolerance `HomeJoinPlanner` uses (`assembly_overlap`).
3. **Hazard strip, as part of the guarantee pass** (required for the "safest" requirement): variants to standard, no encounters, no arc zones, no breach or fire zones, on the layout and the slice, then re-run `ValidateHomeStart`.
4. **Minimum 3 non-connective rooms** and **at least 3 free interior slots outside the start room** (cheap; both already hold for almost all seeds).
5. **Reachability from the start room to every supply cache room** by standing rules (start-to-dock is trivial here).
6. **Fallback if extension 1 cannot reach the bar:** a whitelist of templates with a high pass rate (only `spine`, `bifurcated` and `stacked` pass at all today), or a few authored home layouts, so the variety requirement is met by choosing among known-good layouts rather than by retrying.

## Recommendation: conditional GO

- **GO for PR C (restore the generated-home boot behind a flag).** The plumbing risk is low: generation succeeds on every seed, `Pristine` is clean of wreck damage and leaves a single-sub lifeboat blocker, and Continue and `user://runs` already work. PR C keeps the seed-17 tests on the golden ship.
- **NO-GO for flipping New Run to generated homes** until the sweep, rerun after extensions 1 to 3, meets the bar: at least 95% of seeds produce a valid home within 8 attempts with at least 3 non-connective rooms, at least 3 free slots, start reaching the dock by standing rules, a calibrated lifeboat fit, and zero hazards after the strip. Today that figure is 7% on the lifeboat fit and 0% on hazards, because the strip does not exist yet.
- **Why 95% within 8 attempts:** the New Run reseed loop makes up to 8 tries, so the per-attempt pass rate must be about 31% for 95% overall. A New Run that fails to generate a home would be a hard failure for the player.

## Caveats

- Lifeboat fit has medium confidence (see Method). Only one biome/difficulty pair was swept; both remain locked in the New Run UI.
- `StartSceneBuilder.HOME_CONDITION` (Damaged) was not changed; PR C will pass Pristine.
- No PlayMode or live-scene proof exists for any generated home. The 30-minute idle safety test (`HomeSpawnSafetyTests`) is still golden-only.
