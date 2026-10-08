# Milestone A closing regression matrix

Milestone A New Run supports any seed from 1 to 2147483647 (Phase 1.5), biome
**breach_field**, difficulty **standard**, and the golden `coherent_ship_001`
hub. The seed drives the world and the first wreck; the home is the same for
every seed. Other biomes, difficulties and out-of-range seeds fail closed.
First-away generation tries production seed **42**, then **777** only if the
first candidate fails the complete contract, at the contact's size and condition
(the D9 patch makes both pass). The seed varies the contacts, not the first
wreck's hull: wrecks built from a contact's own seed are larger and deadlier than
the validated hull, so that change is parked (see `decisions.md`, Phase 1.5).

The production matrix tests both preferred seeds over lifeboat/small/medium
sizes and pristine/damaged/wrecked conditions (18 cases). Seed 42 passes all
three medium conditions; its six smaller cases lack the required encounter.
Seed 777 has encounters and loot but no required fire/breach hazard, so all nine
cases reject. Checks of each non-hazard requirement isolate the hazard failure; the live
contract is never relaxed. Listing 777 as a preferred candidate does not make
it an accepted fallback. Existing candidate-order and no-candidate denial tests
remain. Only the default accepted first-away journey is physically playtested;
these 18 cases verify generated content/standing validation, not 18 expeditions.

## Class progression coverage

Updated for Phase 1.4 (D6, universal basic repair). All eleven classes (eight
initial, three unlockable) now reach travel readiness on the supported hub.
`EveryStartingClassCanRepairTheFlightPathAtSkillDependentQuality` performs the
finite cache search and the normal timed, parts-and-tools-gated repairs for each
class, on both the plain route and the route that first completes the first
three hub objectives. No skills or XP are granted by the tests.

Repair skill no longer gates a repair; it sets its speed and quality:

- Meeting a part's skill requirement repairs it to full health, a little faster
  for each level above the requirement.
- Each missing level adds 25% to the repair time and lowers the health the part
  is left at: `0.5 + 0.5 * (skill + 1) / (requirement + 1)`, never below 0.5
  (the operational threshold).
- A part repaired below full health stays marked as reduced quality. A survivor
  whose skill reaches a higher quality can repair it again (parts are consumed
  again) to bring it up.

The hub reactor core needs repair 4, so the health it is left at, using the
class's starting repair skill, is:

| Class | Starting repair | Reactor core health after repair | Travel systems ready |
|---|---:|---:|---|
| Mechanic | 4 | 1.0 | Yes |
| Engineer | 3 | 0.9 | Yes |
| Scientist | 2 | 0.8 | Yes |
| Medic, Pilot, Security, Salvage Captain | 1 | 0.7 | Yes |
| Cook, Communications, Field Medic, Signal Specialist | 0 | 0.6 | Yes |

Skill earned from earlier repairs only raises these values. Repairs run lowest
requirement first. This is not eleven physical Unity expeditions: the matrix
positions interactions explicitly and advances actual work channels in the
engine-free harness. Physical walking/survival/combat is checked separately for
the default Engineer.

Open interaction: a repair at health 0.6 leaves the power system below the
~0.77 health the sustenance allocation needs to reach its 0.5 ratio, so a
skill-0 survivor repairs the flight path but cannot yet power hydroponics and
the recycler. `EveryClassCanStartFoodProductionAfterRepairingTheReactor` pins
this (the mechanic control passes; the cook case is ignored until the Phase 1.3
power budget change).

Completing all hub objectives fixes objective-linked systems but intentionally
ends extraction; it cannot be used as a shortcut into a continuing first-away
run.

## Combat acquisition

The existing unarmed path names crowbar as a fallback, but the authoritative
weapon resolver requires an equipped weapon. No loot table offered crowbars.
The finite maintenance cache now adds one existing crowbar; its ordinary loot
callback auto-equips an empty primary hand. Damage, reach, facing, cooldown,
enemy tuning and ammo behavior remain unchanged. Exact loader parity now pins
all **ten** authored item/quantity stacks. This is an in-world acquisition fix,
not a test inventory grant or an unrestricted unarmed weapon.

The focused GPU Unity journey passes: Title/New Run, real walking, finite loot,
earned repair progression, timed work, guarded first-away travel, defeating a
real generated encounter, wreck loot, home return, Save/Continue and an actual
saved-wreck revisit with no defeated-enemy respawn. No enemies or damage are
injected. Normal gameplay movement and host attack entry points retain reach,
facing, LOS, cooldown and modal gates. The fixture uses the existing empty-hand
auto-equip behavior rather than inventing a loadout. The original extraction,
death and restart acceptance assertions remain separate and unchanged.

Focused evidence: `builds/logs/closing-combat-journey-v2.xml`; production seed
matrix: `closing-seed-matrix.trx`; final Core: **732 passed, zero failed**,
`closing-final-core-v3.trx`; final Unity Edit Mode: **984 passed, zero failed,
17 existing companion fixture skips** out of 1001,
`closing-final-editmode-v2.xml`.

Final GPU Unity Play Mode aggregate: **51 passed, zero failed or skipped**,
`closing-final-playmode.xml`, including the combined encounter/return/save/revisit
and existing natural extraction/death/results/restart tests.

The Windows development Mono player was rebuilt with the final cache data:
**487.6 MB, zero errors**, version 0.1.0 (`closing-final-build.log`). Open
`F:\tmp\synaptic-sea-playable\SynapticSea` in Unity 6000.6.0f1, open Boot and
select the default Engineer New Run. The built player is
`builds/StandaloneWindows64/dev/TheSynapticSea.exe`. F attacks along the last
movement heading; E searches/interacts and WASD moves. Search the upper-deck
maintenance cache to acquire the finite kit and auto-equip the crowbar before
the expedition. Previously searched saves receive no retroactive supplies.

The final standalone boot/title smoke check passed with the expected dev stamp
and no detected errors (`closing-final-player-smoke.log`). It is not a manual
standalone expedition; physical gameplay was verified in GPU Unity Play Mode.

## Decisions still required

1. ~~Should the six nontechnical classes be hub-extraction-only?~~ Decided by
   D6 (Phase 1.4): every class can repair at reduced quality, so every class
   can perform first-away travel. Class starting skills and cross-training
   penalties are unchanged.
2. Is a guaranteed alternate first-away seed/size required? The declared 777
   candidate does not currently provide the required hazard. Keep the current
   fail-closed behavior, or commission a supported alternate authored candidate;
   no arbitrary replacement seed or added hazard has been selected here.
3. Colony scope remains pending: finite harvest/reserve/alert/funded defenses
   versus immediate autonomous collectors and territorial spread.
4. The reviewed crawler remains draft until owner approval. Production creature
   rendering therefore retains the validated placeholder fallback.

No new upstream package revision, owner approval or proprietary asset publication
is part of this regression pass.
