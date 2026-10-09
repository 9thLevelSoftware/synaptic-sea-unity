# Playtest QA — Milestone A New Run / first away

Verify path for the New Run + first-away launch contract (REQ-SLICE-001, `docs/game/features/vertical_slice_v1.md`, `docs/game/features/generated_seed_boarded_slice.md`). FC crafting / derelict economy is out of scope.

Automated coverage: `dotnet test tools/dotnet/SynapticSea.Core.Tests --filter MilestoneALaunchContract` (hub resolve, fail-closed launch params, preferred-seed order, attach-path first away, travel deny, `StartSceneBuilder` null-with-log) and `--filter RandomSeedNewRun` (any seed accepted, seeded world, golden home for every seed, save/Continue with the same contacts, the first wreck qualifies for the contacts of many worlds; set `SYNAPTICSEA_SEED_SWEEP=200` for a wider sweep). PlayMode: `TitleNewRunRequestBootsTheMilestoneAHub`, `TitleNewRunBootsPlayableAndQuitReturnsToTitle`, `NewRunWithARandomSeedBootsTheGoldenHubOnASeededWorld` and `ResultsNewRunRollsAFreshSeedAndKeepsTheClassAndPacing`.

**Phase 1.11 (current):** the Title's New Run at scaled pacing (the default, 1 real minute = 1 game hour) starts in a **home generated from the seed**, not the golden hub: a Pristine layout with the lifeboat docked on its exterior edge, the lifeboat repair kit plus food and water for a 30 game hour excursion placed in seeded rooms, a calm start (no threats, fire, breaches or hazardous rooms), and the four onboarding objectives by room role. The golden `coherent_ship_001` hub is still what direct open, `RunLaunchRequest.GoldenShip()`, the scripted golden routes and real-time pacing ("Off (real time)" in the setup, which says so on screen) boot. Safety evidence: `docs/playtest/home-safety-1.11.md`. The sections below that say "hub" describe the authored hub path (acceptance 1 applies to it with pacing Off or a direct open).

**Phase 1.5:** a New Run takes any seed from 1 to 2147483647. The title opens on a random seed (Randomize rolls another; Results "New Run" rolls a fresh one). The seed drives the Synaptic Sea world (scanner contacts and their sizes and conditions, sea graph). The first away wreck still uses the validated hull (seed 42, else 777) at the contact's size and condition. The home is the golden `coherent_ship_001` for every seed: its systems damage, loot, tutorial and spawn safety are unchanged. Biome (`breach_field`) and difficulty (`standard`) stay fixed, shown as "(fixed)" with a reason on the setup screen, until the hardened and other-biome homes are playtested.

## Acceptance

### 1. Title New Run → a generated home (scaled pacing) or the hub `coherent_ship_001` (real-time pacing, direct open)

1. Boot to Title. Leave difficulty at **standard**.
2. Choose **New Run**. The setup opens on a random seed with the biome and difficulty rows marked "(fixed)".
3. Start the run (any seed, or Randomize first). The log line `[PlayableBootstrap] booted ... seed=<seed>` shows the seed you chose.
4. Confirm the home: with scaled pacing it is a generated home (its layout is under `user://runs/<id>/` and the log line `[PlayableBootstrap]` names the seed; a different seed gives a different layout); with pacing **Off** it is the golden `coherent_ship_001` (not `smoke/seed_000017`).
5. Move, interact, and confirm the HUD is live.

### 2. First away evaluates 42 then 777

1. On the hub, repair power / navigation / scanners / propulsion so travel is legal.
2. Open Scanner and Travel to the first in-range marker. Do not overwrite the marker seed by hand.
3. The boarded wreck must be a generated `procgen-*` layout, **not** hub golden `coherent_ship_001`.
4. Boarded seed is **42** or **777** (42 first; 777 only if 42 fails the complete contract), at the contact's size and condition. The first wreck always carries the `first_wreck_stores` crate.
5. Two different seeds show different scanner contacts; the same seed shows the same contacts again after Continue.

### 3. Boarded away contract

On the boarded wreck, confirm:

- You arrived through the attach/travel path (`away_from_start` is a result of docking, not a debug flag).
- Standing start→goal navigation exists.
- At least one interior loot slot (center/wall).
- At least one objective.
- Wreck overlay if the boarded condition is DAMAGED or WRECKED.
- Fire-or-breach + loot + ≥1 encounter (first-run contract).

### 4. No seed passes → deny, hub unchanged

Forced in automation by setting preferred seeds to invalid values. Manually: if both preferred seeds fail generation/contract, Travel must:

- Fail with a readable scanner/status reason containing `first_run_contract_unsatisfied`.
- Leave you on the hub (same ship, same position).
- Not mark the marker generated / not change the marker seed.

### 5. `StartSceneBuilder` stays null-with-log

New Run must still pass 1–4. `StartSceneBuilder.Build` remains the legacy life-boat+derelict path and returns null (log: no dock room) for seeds without a dock. Do not invent docks.

### 6. F5 / F9 / Continue

Save/load may only show the already-pinned ≤22 allowed save-rebuild divergences (`SessionSaveParityTests.LoadDerivedPaths`). Do not expand that list for this milestone.

### 7. Fail closed + BlockedRoute parity

- A New Run request with an out-of-range seed (below 1 or above 2147483647), a non-slice biome, or a non-slice difficulty (example: seed 99, `dead_fleet`, `hardened`) must **not** silently load `seed_000017` or the wrong hub. Expect a readable `non_slice_launch` reason and a return to Title. The setup screen cannot produce these (the rows are fixed and the seed is clamped); only a hand-built request can.
- `BlockedRoute_*` markers keep Godot `96ecb2b0` parity for this slice (stay collidable after powered gates open if that is what Godot does).

### 8. Idle home is passively safe

Stand in the start room after Title New Run (a generated home at scaled pacing, or `coherent_ship_001` with pacing Off). You must not die from unengaged hunters, ship-wide radiation, hunger, or unattended fire while only present. Automation covers 30 simulated minutes with game session deps: `HomeSpawnSafetyTests` for the golden hub, and `GeneratedHomeSafetyTests` for generated homes at 30x, 60x and 120x. Do not retune survival JSON.

## Godot `96ecb2b0` note

At commit `96ecb2b0`, `FirstRunContract.pick_seed` falls back to preferred[0] and `_apply_first_run_contract_to_marker` always mutates the marker via `ShipLayoutGenerator`. Live Godot after that commit, and the locked Systems Designer decision, deny travel when no candidate passes and evaluate through production `ShipGenerator`. Unity keeps `PickSeed` fallback for 96ecb2b0 parity tests and uses the deny / ShipGenerator gate on the live `travel_to` path.
