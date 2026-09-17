# Tick order

`_process(delta)` (`scripts/procgen/playable_generated_ship.gd` @ 96ecb2b0, lines 8525-8576) had two branches that ran
the same systems in different, order-sensitive sequences. The port keeps one stage set
(`Core/Session/TickStages.cs`, `TickOrder.Stages`) and two explicit order tables, `TickOrder.HomeOrder` and
`TickOrder.AwayOrder`. `RunSession.Tick(in TickContext)` runs them.

## Every frame, before the branch

1. `world_time += delta`. This always runs, even before start and after completion.
2. `run_play_time_seconds += delta`, but only when the slice has started and is not complete.
3. The branch is chosen from `away_from_start`. Both branches return early when the slice has not started or is
   complete. The home branch also returns early when `oxygen_state` is null.

## Away branch (`TickOrder.AwayOrder`)

| # | Stage | Godot call |
|---|---|---|
| 1 | oxygen | `_refresh_oxygen_state(false, delta)` |
| 2 | threat | `_tick_threat_runtime` |
| 3 | sanity_hallucination | `_tick_sanity_and_hallucinations(delta, false)` |
| 4 | active_fire | `_tick_active_fire` |
| 5 | survival_attrition | `_tick_survival_attrition` |
| 6 | player_vitals | `_refresh_player_vitals` (away only) |
| 7 | tracker_status | `_refresh_tracker_system_status_lines` (away only) |
| 8 | field_craft | `field_crafting_state.tick` then `_on_field_craft_completed` |
| 9 | autosave | `_tick_autosave_policy` |
| 10 | audio | `_tick_audio_runtime` (audio tick, `_refresh_audio_state`, footsteps) |
| 11 | present_ships | `_tick_present_ships` (ShipRuntime frame band; hub SLOW band calls `_recompute_expanded_ship_systems`) |
| 12 | recharge_port_power | `extinguisher_recharge_port.set_powered(active manager power)` (away only) |
| 13 | food | `_tick_food_runtime` |
| 14 | ammo_consumable_decay | `_tick_ammo_and_consumable_decay` |
| 15 | electrical_arc | `_tick_electrical_arc` |
| 16 | work_action | `_tick_work_action` |
| 17 | work_action_hud | `_refresh_work_action_hud`, raised as `SessionEvents.WorkActionHudState` |
| 18 | tooltip_focus | `_refresh_tooltip_focus`, raised as `SessionEvents.TooltipQuery` |

## Home branch (`TickOrder.HomeOrder`)

| # | Stage | Godot call |
|---|---|---|
| 1 | autosave | `_tick_autosave_policy` |
| 2 | threat | `_tick_threat_runtime` |
| 3 | present_ships | `_tick_present_ships` |
| 4 | active_fire | `_tick_active_fire` |
| 5 | field_craft | `field_crafting_state.tick` then `_on_field_craft_completed` |
| 6 | oxygen | `_refresh_oxygen_state(false, delta)`; this also refreshes the tracker lines and player vitals |
| 7 | electrical_arc | `_tick_electrical_arc` |
| 8 | ammo_consumable_decay | `_tick_ammo_and_consumable_decay` |
| 9 | survival_attrition | `_tick_survival_attrition` |
| 10 | sanity_hallucination | `_tick_sanity_and_hallucinations(delta, in_safe)` with `in_safe = not away and not breach_open` |
| 11 | food | `_tick_food_runtime` |
| 12 | audio | `_tick_audio_runtime` |
| 13 | work_action | `_tick_work_action` |
| 14 | work_action_hud | `_refresh_work_action_hud` |
| 15 | tooltip_focus | `_refresh_tooltip_focus` |

## Location-specific stages

Three stages run in only one branch. Each declares its `StageScope` and a reason in code:

- **player_vitals and tracker_status (away only).** At home, `_refresh_oxygen_state` already calls
  `_refresh_player_vitals` and `_refresh_tracker_system_status_lines`, and the Godot home branch has no separate calls.
  Adding them would double-tick `PlayerVitalsModel`.
- **recharge_port_power (away only).** On a derelict, the derelict's own power gate must win over the hub "stations"
  allocation. At home, the SLOW-band recompute inside `present_ships` is the port's only power source.

`TickOrderTests` fails if any other stage appears in only one table, or if a table drifts from the Godot branch. This
prevents the "system wired in only one branch" class of bug, which caused PR #42 through #44 in Godot.

## After the branch

The interaction nodes' own `_process` runs every frame, regardless of the early returns, because Godot processed them as
children after the coordinator. These are the dock barrier breach channels, repair channels, breach seal channels, fire
suppression channels, and extinguisher recharge. See `RunSession.ProcessInteractableNodes`.
