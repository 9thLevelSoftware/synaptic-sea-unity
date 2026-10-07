# Proposed first colony resource and defense slice

This is a proposal for review, not an implemented mechanic. The game currently has
saved per-ship `WebInfestationState` coverage, growth, recession and hull damage;
`biomatter_tangle` junk converts to two `biomatter_residue` and one `reactive_gel`;
work channels already handle progress, noise, completion and inventory yields.
`ThreatRuntime` owns encounters, movement, perception, combat and saved threat summaries.
The new Critter adapter supplies validated visual recipes; it does not supply colony AI.

## Player-visible behavior

An infested derelict exposes a small, finite number of visible biomass growths.
Holding the existing work input with a cutting tool harvests one growth. Completion
grants the existing tangle item, depletes that growth, raises a visible colony alert,
and emits the existing work noise. Nearby threats react through existing perception.
Enough disturbance triggers a telegraphed defense response: the colony spends a
finite biomass reserve to reinforce one existing, reachable encounter marker.
The player can harvest cautiously, fight the response, or cut free and leave.

## Exact architectural fit

* Add a small pure `ColonyState` beside `WebInfestationState`: reserve, alert,
  growth/source depletion, response cooldown and deterministic response sequence.
* Store it with each ship's existing hazard summary; absent fields default to zero
  for legacy saves. Travel and continue restore it with that ship, not globally.
* Use a dedicated harvest interactable and a work-channel completion resolver.
  It reuses work reach/tool/progress/noise and inventory delivery, without treating
  arbitrary walls, loot containers or player cargo as colony-owned biomass.
* Seed finite growth IDs and response markers from existing deterministic layout
  data. Cosmetic modules and purchased meshes do not become resource authority.
* Spend reserve once per completed response, spawn through `ThreatRuntime`, and
  use the live Critter adapter when an approved library is available. Existing
  placeholders remain the fallback. No draft asset approval is required for play.
* Add two status lines to existing hazard/readability UI: reserve and alert/response.

## Bounded initial rules to review

Colony-owned marked growths are the only new harvestable biomass source. Reserve
grows slowly only while attached to the web, up to a cap; depleted sources do not
regrow during this first slice. Harvest removes a finite source and adds disturbance.
Alert decays while undisturbed. A response requires a threshold, enough reserve,
a cooldown and a reachable marker away from the player. Cap reinforcements and
living organic threats per ship. A failed placement spends nothing and does not
spin every frame. Existing fixed encounter markers and Milestone A seed/biome/
difficulty/extraction guards stay in place.

These are new scripted resource and defense rules. They do not implement learned
adaptation, force-limited movement, mass/energy evolution or arbitrary biomass
assimilation. Those require a later physical model and design review.

## Acceptance criteria

1. A player can discover, harvest and receive one visible growth's yield through
   normal movement/input; leaving reach or releasing input interrupts work safely.
2. The source cannot pay twice, including save/load, travel away/return and restart.
3. Identical seed and action history produce identical source and response IDs.
4. No response occurs below threshold, without reserve, inside cooldown, beyond
   the cap or without a valid reachable marker. Spending cannot go below zero.
5. A visible warning precedes reinforcement; sealed-room perception and normal
   combat rules still apply. Missing creature assets do not prevent the response.
6. Cutting free halts reserve growth; existing infestation recession still works.
7. A natural Play Mode journey harvests, provokes a response, survives or dies,
   saves/continues and returns without duplicating sources or changing hub guards.

The consequential choice is source ownership/depletion: this proposal limits it
to authored growth markers and finite harvests. Implementation should follow review
of that boundary and response pressure, before broader biomass assimilation.
