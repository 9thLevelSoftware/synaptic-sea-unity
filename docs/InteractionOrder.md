# Interaction order

`_on_player_interact_requested` (`scripts/procgen/playable_generated_ship.gd` @ 96ecb2b0, lines 7953-8077) became one
ordered table, `Core/Session/InteractionRegistry.cs`. When the player presses interact, handlers run top to bottom and
the first one that claims the request wins. Each handler declares a scope:

- `Both`: runs at home and on a boarded derelict.
- `Home`: runs only when `away_from_start == false`.
- `Away`: runs only aboard a derelict.

Authored deck transfers and closed-door priority supplement the original Godot branches. When nothing claims the request, the dispatcher plays
the soft-miss cue (`ui.panel.close`).

`Tests/EditMode/Session/InteractionOrderTests.cs` parses the table below and requires it to equal the registry for both
locations. The test also pins both current chains, including the documented Unity traversal fixes. Edit this table and the
registry together.

<!-- interaction-order:begin -->
| # | Handler | Scope | Godot call |
|---|---|---|---|
| 1 | dock_barrier | Both | `dock_barriers`: `b.try_start(player)` on unopened barriers |
| 2 | authored_portal | Both | `_try_authored_portal_interact(player)` (closed doors before stations; open doors yield to ordinary targets) |
| 3 | deck_transition | Both | Authored `vertical_connections`: safe bidirectional player landing |
| 4 | bridge_terminal | Both | `bridge_terminals`: `t.try_login(player)` |
| 5 | fire_suppression_point | Both | `fire_suppression_points`: `fp.try_start(player)` |
| 6 | repair_point | Both | `repair_points`: `rp.try_start(player)` |
| 7 | breach_seal_point | Both | `breach_seal_points`: `sp.try_start(player)` |
| 8 | crafting_station | Home | `crafting_stations`: `st.try_interact(player)` |
| 9 | production_station | Home | `production_stations`: `st.try_interact(player)` |
| 10 | loot_container | Both | `loot_containers`: `lc.try_interact(player)` |
| 11 | hatch_bypass | Away | `_try_bypass_nearest_hatch()` |
| 12 | hatch_reseal | Away | `_try_reseal_nearest_hatch()` |
| 13 | derelict_objective | Away | `derelict_interactables`: `it.try_interact(player)` |
| 14 | tool_pickup | Home | `_try_tool_pickup_interact(tool_pickup, player)` |
| 15 | junction_calibrator_pickup | Home | `_try_tool_pickup_interact(junction_calibrator_pickup, player)` |
| 16 | home_objective | Home | `interactables`: `interactable.try_interact(player)` |
| 17 | hangar | Both | `_try_hangar_interact(player)` |
| 18 | cargo_deposit | Both | `_try_cargo_deposit(player)` |
| 19 | cart | Both | `_try_cart_interact(player)` |
| 20 | work_yield_drop | Both | `_try_work_yield_drop_interact(player)` |
| 21 | work_action | Both | `_try_work_action_interact(player)` |
<!-- interaction-order:end -->

## Resulting chains

**Home:** dock_barrier, authored_portal, deck_transition, bridge_terminal, fire_suppression_point, repair_point, breach_seal_point, crafting_station,
production_station, loot_container, tool_pickup, junction_calibrator_pickup, home_objective, hangar, cargo_deposit, cart,
work_yield_drop, work_action. If none claims the request, the soft-miss cue plays.

**Away:** dock_barrier, authored_portal, deck_transition, bridge_terminal, fire_suppression_point, repair_point, breach_seal_point, loot_container,
hatch_bypass, hatch_reseal, derelict_objective, hangar, cargo_deposit, cart, work_yield_drop,
work_action. If none claims the request, the soft-miss cue plays.

## Claim rules

These rules carry over from the Godot handlers:

- **Soft denies still claim.** An in-range tool pickup that is already owned claims the request and plays a deny cue.
  So does a yield pile that can't be scooped, a locked hatch or portal, and a workable wall with no cutting tool. The
  request does not fall through to the miss cue.
- **Channels claim.** A repair point, breach seal point, fire suppression point, or broken dock barrier that is already
  channeling returns true, so lower-priority handlers do not fire.
- **Bridge terminals (port change).** A terminal of the ship the player already pilots does not claim. Godot's
  `try_login` claimed every in-range press, and the life boat's repair and fire suppression points share its command
  room's centre, so they were unreachable (`docs/port-status.md` decision 33).
- **Exterior portals.** An authored exterior portal returns the result of `travel_home()`.
- **Station placement (port change).** Godot put the home crafting and production stations (rows 6 and 7) on the first
  structural nodes, the airlock and corridor floor-cell centres, so they claimed interact next to the spawn ahead of
  pickups and objectives. `Core/Session/StationPlacer.cs` places them by room role instead, only where their 1.8 m area
  overlaps no other interaction area and the player start, never on a doorway cell, and falls back to the first free
  legacy position. Closed doors now precede stations; open doors yield to reachable ordinary interactions.
