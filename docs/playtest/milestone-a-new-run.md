# Playtest QA — Milestone A New Run / first away

Verify path for the locked New Run + first-away launch contract (REQ-SLICE-001, `docs/game/features/vertical_slice_v1.md`, `docs/game/features/generated_seed_boarded_slice.md`). FC crafting / derelict economy is out of scope.

Automated coverage: `dotnet test tools/dotnet/SynapticSea.Core.Tests --filter MilestoneALaunchContract` (hub resolve, fail-closed launch params, preferred-seed order, attach-path first away, travel deny, `StartSceneBuilder` null-with-log). PlayMode: `TitleNewRunRequestBootsTheMilestoneAHub` and `TitleNewRunBootsPlayableAndQuitReturnsToTitle`.

## Acceptance

### 1. Title New Run → hub `coherent_ship_001`

1. Boot to Title. Leave difficulty at **standard**.
2. Choose **New Run**.
3. Confirm the hub is golden `coherent_ship_001` (not `smoke/seed_000017`).
4. Move, interact, and confirm the HUD is live.

### 2. First away evaluates 42 then 777

1. On the hub, repair power / navigation / scanners / propulsion so travel is legal.
2. Open Scanner and Travel to the first in-range marker. Do not overwrite the marker seed by hand.
3. The boarded wreck must be a generated `procgen-*` layout, **not** hub golden `coherent_ship_001`.
4. Boarded seed is **42** or **777** (42 first; 777 only if 42 fails the complete contract).

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

- Title New Run with a non-slice seed, biome, or difficulty (example: seed 99, `dead_fleet`, `hardened`) must **not** silently load `seed_000017` or the wrong hub. Expect a readable `non_slice_launch` reason and a return to Title.
- `BlockedRoute_*` markers keep Godot `96ecb2b0` parity for this slice (stay collidable after powered gates open if that is what Godot does).

## Godot `96ecb2b0` note

At commit `96ecb2b0`, `FirstRunContract.pick_seed` falls back to preferred[0] and `_apply_first_run_contract_to_marker` always mutates the marker via `ShipLayoutGenerator`. Live Godot after that commit, and the locked Systems Designer decision, deny travel when no candidate passes and evaluate through production `ShipGenerator`. Unity keeps `PickSeed` fallback for 96ecb2b0 parity tests and uses the deny / ShipGenerator gate on the live `travel_to` path.
