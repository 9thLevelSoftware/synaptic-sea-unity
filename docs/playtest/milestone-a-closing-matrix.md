# Milestone A closing regression matrix

Milestone A New Run supports seed **17**, biome **breach_field**, difficulty
**standard**, and the golden `coherent_ship_001` hub. Other New Run parameters
fail closed. First-away generation tries production seed **42**, then **777**
only if the first candidate fails the complete contract. These are away seeds,
not additional supported title seeds.

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

The catalog has eight initially selectable classes and three unlockable classes.
This bounded matrix covers all eight initial classes on the supported hub with
fresh meta progression. It performs the finite cache search and normal timed,
resource/skill-gated repairs, including available side repairs. A second path
also performs the first three existing hub objective sequences, stopping before
the final reactor objective triggers extraction. No skills or XP are granted by
the tests, and class multipliers/cross-training rules remain intact.

| Class | Starting repair | Repair route earned level | Travel systems ready | Partial-objective route earned level | Travel systems ready |
|---|---:|---:|---|---:|---|
| Engineer | 3 | 5 | Yes | 4 | Yes |
| Mechanic | 4 | 5 | Yes | 5 | Yes |
| Medic | 1 | 1 | No | 1 | No |
| Pilot | 1 | 1 | No | 1 | No |
| Scientist | 2 | 3 | No | 2 | No |
| Cook | 0 | 0 | No | 0 | No |
| Security | 1 | 1 | No | 1 | No |
| Communications | 0 | 0 | No | 0 | No |

All eight classes can finish the existing hub objective/extraction sequence in
the Core contract. This is not eight physical Unity expeditions: the class
matrix positions interactions explicitly and advances actual work channels in
the engine-free harness. Physical walking/survival/combat is checked separately
for the default Engineer. The table reports travel-system readiness, not a full
travel/survival pass for Mechanic.

The nontechnical paths stop with no eligible remaining repair using the cache
supplies. Scientist reaches repair three but cannot repair the level-four
reactor. Repair-one classes cannot reach repair two with their two initial
repairs, and repair-zero classes cannot start either. Completing all hub
objectives fixes objective-linked systems but intentionally ends extraction;
it cannot be used as a shortcut into a continuing first-away run.

Existing alternatives were inspected: books target welding/diagnostics/
fabrication/medicine rather than repair; hub drydock grants one repair level
after an expensive prerequisite chain; structural work uses actual tools,
materials and targets rather than a repeatable free training station. No
verified fresh-run universal first-away path is declared for these six classes.
Unlockable classes and purchased hub-upgrade combinations are outside this
bounded matrix; their owner/unlock gates have not been changed.

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

1. Should the six nontechnical classes be hub-extraction-only in this milestone,
   or should each be able to perform first-away travel on a fresh run? If the
   latter, choose an intended nontechnical route (for example crew repair or
   bounded training) before implementing it. Changing class starting skills,
   reducing repair gates or removing cross-training penalties would alter the
   declared challenge balance and has not been done here.
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
