# One reclaimed home extension and assembly mobility

## Implementation checkpoint

The first code checkpoint implements reciprocal graph validation and atomic rejection of ancestor cycles, duplicate members and occupied named endpoints. New docking edges retain exact versioned local endpoints and explicit host ship identity. Both outbound travel and home return preflight docking before detaching or clearing active ship state. World loading preflights saved records before changing live roots and restores edges in parent-first order; old canonical docking saves keep their compatibility path. This is connection infrastructure, **not a delivered reclamation mechanic**.

Still required: resolve the existing `plating` crafting output versus `hull_plate`/legacy-alias wall-work input (reuse and consume the real owned crafting material rather than granting inventory), exterior-site footprint qualification, interruption-safe weld interaction and material consumption, physical multi-root join/locks/navigation, retained home-extension occupancy and simulation, isolated lifeboat ownership migration, aggregate mobility/travel UI, and a real walking/save/independent-craft acceptance journey. The existing persistent-survival executable remains the runnable delivered build until these pieces pass together. No new build is claimed by this checkpoint.

## Bounded player path

1. Continue the existing life, survey and physically board a derelict. Claiming its bridge still requires working propulsion; navigation, scanners, power and load capability independently gate travel.
2. Repair a suitable vessel and its connection boundary using ordinary tools, materials and interruptible work. Fly it home through existing guarded travel with its docked shuttle carried in the graph; do not relocate an unpowered remote wreck through a reclamation button.
3. Moor at one separate compatible exterior home site, leaving the existing shuttle berth intact. Reject unsupported floor contact, occupied sites and overlap with any existing assembly member before moving/attaching. An unsuitable hull remains unsuitable; do not stretch, delete interiors or silently reposition it to force a fit.
4. Hold-interact secure/weld work consumes existing hull plates, requires an existing welding tool and earned repair eligibility, can be interrupted, and revalidates the site before committing. Completion creates durable home membership and a physically traversable connection. Failure/cancellation creates no membership and consumes no completion materials.
5. Walk both directions, save/Continue while inside the extension, depart in the independent small craft, return, and retain the extension's exact layout, loot, doors, systems, damage and connection identity.

This uses one qualified exterior attachment boundary rather than expanding the starter layout. A suitable site's local endpoints, outward facing, footprint, stable ID and contract version are explicit. Old canonical shuttle docks retain their legacy contract. Secured joins are distinct from temporary mooring and hangar storage. There is no invented towing action.

## Invariants

- IDs identify ship instances and connection sites, not renderer names or list order. A site may be occupied once. The directed docking graph is acyclic, reciprocal and contains each member once; reject ancestor cycles before undocking or changing transforms.
- Restore exact versioned local endpoint contracts for new edges, validate endpoints/member identity and preserve old canonical-port migration. Unsupported records fail with an explanation; no save deletion/reset or automatic repair/approval.
- Retain connected home scenes and simulate each present vessel once. Occupancy selects the actual owning ship; loot, repairs, component/module state, hazards and perception must not write to another ship's same-named room/module.
- Collision/navigation and wall cutaway compose all physically connected retained roots. Locks and unsupported void remain authoritative. Ordinary player walking and reach/LOS checks remain acceptance requirements.
- Services remain conservative and local. A connection is not an oxygen/power/infestation conduit. No scalar averaging or free reactor power. Inter-vessel atmosphere/containment transfer needs a separate deliberate model.

## Tunable mobility model v1

Use distinct compiled floor ownership as the deterministic hull area source (4 m cells, every deck counted, visual overlays excluded). Initial dry mass coefficient: **100 gameplay kg per square metre**. Rated propulsion supports **125 gameplay kg per square metre** for a vessel's functioning authored propulsion installation; this gives an intact unloaded independent hull limited payload headroom. These are versioned gameplay tuning coefficients, not certified physical masses, Newtons or rigid-body acceleration.

Count dry mass, uniquely owned installed components and ship cargo once per mechanical member. Include stowed/carried craft as payload but exclude stowed engines. Player carried cargo is counted once with the departing craft. Effective propulsion uses the installation's own control condition and adequate local power; zero/offline propulsion or unavailable local power contributes nothing. Expose member/mass/engine ownership, excluded reasons, effective supported load and margin. Increase cargo/connected hull load without minting propulsion. Independent-craft departure detaches from its parent and carries its descendants; connected-assembly departure uses a validated root and keeps member relative transforms.

The old home/lifeboat shared manager must be migrated explicitly. Do not count it twice or grant an extra repaired engine from two references. Preserve the already-earned onboarding commissioning state and existing independent-shuttle travel. New per-ship managers and propulsion ownership must be saved, with an explicit compatibility path for old summaries.

## Acceptance

Pure/property tests: graph mutation atomicity, ancestor/self/duplicate/missing endpoints, exact endpoint save/restore and legacy migration; area/component/cargo mass counted once; damaged/unpowered/repair-restored engines; increased load blocking travel; independent shuttle versus assembly departure; malformed/non-finite data fails closed.

Actual scene/player tests: suitable and unsuitable hulls, no overlap/unsupported contact, door and connection traversal both ways, interrupted work, real material/tool/skill gates, save/Continue inside the extension, independent expedition departure and return, per-ship state isolation, active hazards/encounters, full HUD at default/close/far zoom and bounded live frame-cost sampling. Native desktop input requires availability; test players remain hidden and isolated from the user's save.

## Connection-foundation verification (2026-10-01)

Final changed code: 764 Core tests passed (`builds/logs/home-extension-preflight-final-core.trx`); 1,041 Unity Edit Mode tests passed with 17 existing upstream skips and zero failures (`home-extension-preflight-edit.xml`); all 61 GPU Play Mode tests passed (`home-extension-preflight-play.xml`, finished 05:42:46 UTC). The Play aggregate includes the two real walking/repair/combat/later-expedition/return/Continue journeys. The added unit regressions cover ancestor-cycle and duplicate-member rejection, both-endpoint occupancy, moved/rotated-host exact local restoration, unsupported-save rejection without destroying live state/save, and denied home return without clearing the active vessel. Exact legacy save comparisons remain, with exact assertions for the additive connection contract before projecting it out of the old fixture comparison.

These are Editor GPU and engine-free results, not a fresh standalone acceptance of reclaimed-home welding. No new executable, native desktop input, new extension screenshot, or assembly-mobility acceptance is claimed. The already-delivered persistent-survival build remains unchanged. The wider extension/mobility task remains unfinished as listed above.
