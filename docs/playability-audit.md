# Playability and asset integration audit

This work starts from game main `8dcc95c10ab5e08658546319f51f49b4abd256fc` and companion main `01230d8`, with subsequent local verified slices documented below. The playable bootstrap, hub objectives, inventory, repairs, ship travel, saves, HUD and results screens exist. New Run intentionally uses the Milestone A hub; unsupported seed/biome/difficulty requests remain rejected. The current persistent-life continuation keeps the player alive after onboarding objectives rather than ending extraction; first-away travel does not extract.

## Gameplay changes

* Weapon hits select the nearest living eligible target within the weapon's reach, facing cone, physical line of sight and room visibility. Explicit target IDs cannot bypass these rules. Misses consume ammunition and swing time; cooldown survives saves. Facing follows the last movement direction.
* Enemy damage requires current reach and visibility. Retreating during a telegraph cancels the attack, including anchored tendrils. Presentation fixtures now place targets in actual reach and retain their damage, death and animation assertions.
* Player room signals come from the current world-positioned navigation graph. Sealed hatches affect sight and acoustic attenuation; bypassing them opens the link. Restored ship combat rebuilds spatial perception alongside navigation.
* Hardsuit wear persists in equipment/save state. Hits consume the same profile's durability; depleted destructible armor stops providing positive resistance. Biological resistance with no durability budget remains valid. Threat armor consumes the resolver's returned profile.
* Focus filters skip searched loot, repaired points, opened barriers and the currently piloted bridge. Loot, repair, bridge and other common handlers choose the nearest eligible target within their category rather than insertion order. Authored door HUD/dispatch now share one target. Further source-anchor normalization is still needed beyond the docked repair/fire/seal subset.

## Existing custom asset pipeline

The original projects at `F:\synaptic-sea` and `F:\repos\synaptic-sea-unity` contain generated/custom GLBs under `Assets/Content/Structural/ship_structural_v0`, alongside an `ithappy` kit. Export tooling includes `tools/python/export_structural_glb.py`; wrappers, structural contracts and kit catalogs are separate data. Runtime builds registered module IDs through `KitCatalogResolver` and `ShipSceneBuilder`; it does not discover arbitrary meshes or asset archives.

The active default kit is `ship_structural_v0`. The old ithappy bake's 4×4 visuals for 8×4/4×8 contracts demonstrate missing validation, but are not established as the active runtime failure.

The active custom `corridor_floor_1x2` was a concrete mismatch: its wrapper selected `corridor_floor_1x2.glb`, containing a floor_1x1 collision mesh and roughly 4×4 visual geometry, for a 4×8 contract and collision deck. The existing `corridor_floor_1x2_textured.glb` has a 4×8 visual. A checked-in `corridor_floor_1x2.visual.json` now explicitly binds the intact state to that export and lowers it 0.125 m to align its visible deck with the authoritative collider. No stretching or opening edits are applied. The rebuilt prefab preserves sockets and gameplay collision.

The optional sidecar supports `variants` and `variant_offsets_m` per label (`Intact`, `Damaged`, `Breached`). Other modules and variant selections retain their existing behavior. Existing damaged/breached 1×2 exports contain 2.5 m side structures; they need visual/opening review before replacing the current bindings. Their original files are preserved. The builder now reports floor footprint discrepancies instead of merely recording bounds. The default bake produced 15 prefabs, zero errors and 25 existing proxy/placeholder warnings.

## Local purchased floor example

`tools/import_purchased_megakit.py` imports two allowlisted 4×4 glTF floor sources and their dependencies from the owner's local ZIP. It records SHA-256 provenance in an ignored artifact. `PurchasedMegaKitBuilder.BuildFromVisuals` also accepts local custom GLB/glTF/prefab tiles. It checks units, tiles larger footprints without stretching, aligns deck height, fills genuinely missing material slots, removes imported collision and retains original module sockets/colliders.

This is a **visual overlay for four existing floor module IDs**, not a new generation grammar or automatic adoption of a whole pack. Doors, ramps and other incompatible units/openings are not silently resized. Licensed files, generated local prefabs/materials and the override catalog are gitignored and remain local. Disable the overlay through **Synaptic Sea → Content → Disable Purchased MegaKit Floors (local)** to use the original catalog.

## Validation

* Final Core aggregate: **707 passed, zero failed**, `builds/logs/combat-audit-final.trx`. NuGet vulnerability metadata lookup was unavailable; cached dependencies compiled and the tests executed successfully.
* Unity 6000.6.0f1 Edit Mode aggregate: **915 passed, zero failed**, `builds/logs/unity-audit-editmode-aggregate.xml`. Includes bounds, deck height, non-null materials, preserved collision/sockets and generated seeds 17, 42 and 73.
* Companion contracts/recipes/locomotion selection: **106 passed**, `F:\tmp\critter-audit-results.xml`. This is not the companion's full suite.
* Final Unity GPU Play Mode aggregate: **48 passed, zero failed**, `builds/logs/unity-audit-playmode-aggregate.xml`. Covers movement, interactions, real repair channels, accepted first-away boarding, later biome travel/return, save/load, combat presentation and NavMesh movement. Fixture corrections retain these assertions while supplying real room/reach/cooldown setup and honoring guarded marker eligibility and production kit selection.
* Windows development build: **passed**, Mono, 486.5 MB, zero errors; `builds/logs/build-StandaloneWindows64-dev.log`. Output: `builds/StandaloneWindows64/dev/TheSynapticSea.exe`. The player was built; a manual standalone gameplay session was not run.
* Actual Play Mode gameplay-camera image: `artifacts/screenshots/playable-local-floors.png`. The local floor test verifies deck support, no imported visual colliders, non-null materials and a cap of four renderers per tile. Visual inspection found no obvious floor flicker or floating in the captured view; this is not a GPU performance benchmark or exhaustive visual review of all seeds. A separate generated seed-17 overview is at `artifacts/screenshots/purchased-megakit-seed17.png` (1,631 scene renderers, including existing structural geometry).

Unit and scene tests establish this bounded slice, not a complete game or a continuous natural journey through every terminal outcome. The earlier runs' fixture/material failures are retained in separate logs; the aggregate files above record the final passing runs.

## Run locally

Open `F:\tmp\synaptic-sea-playable\SynapticSea` with `F:\Unity\6000.6.0f1\Editor\Unity.exe`. Open the **Boot** scene and press Play, then select New Run. WASD moves; E interacts; F attacks along the last movement direction; R reloads. The existing HUD/hotbar/pause controls remain available. Use the supported Milestone A defaults.

Alternatively run `F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\dev\TheSynapticSea.exe`, select New Run, and use the same controls.

To reproduce the licensed overlay locally:

```powershell
& F:\Tools\Python312\python.exe tools/import_purchased_megakit.py --archive 'F:\assets\Modular SciFi MegaKit[Source].zip'
```

Then run **Synaptic Sea → Content → Enable Purchased MegaKit Floors (local)** in Unity. To rebuild custom structural prefabs, use the existing structural builder's content menu. Changes to a sidecar require rebaking its prefab. Preserve authored source exports and review variant openings before adding new bindings.

## Remaining high-impact work

1. Resolve the class progression and alternate-candidate decisions exposed by the closing matrix below. The default Engineer now has combined first-away combat/return/save/revisit acceptance; six other initial classes retain fresh-run travel skill blockers. Natural extraction and death/restart retain separate coverage.
2. Review custom damaged/breached geometry, remaining contract/visual mismatches, connector openings and multi-deck transitions across more seeds. The reusable adapter deliberately accepts only compatible planar tiles; it is not a general mesh normalization solution.
3. Complete authoritative interaction selection across all categories and presentation prompts. Common stale/nearest-target defects are fixed, but the dispatch architecture still has category-specific handlers.
4. Complete owner review and configure approved production creature art. The third slice below integrates the pinned companion behind a safe placeholder fallback. Production generation requires approved skeletons/parts; draft assets are not autoapproved by this work.
5. Implement a designed colony biomass/harvest/defense progression slice. Current infestation is scalar coverage/hull pressure and threats are fixed archetypes. The companion provides compatibility/budget generation and gait/IK constraints, not learning, resource harvesting or physical evolutionary adaptation. Those systems remain absent.

The original checkouts and their local settings are preserved. No remote publication, merge or deployment is included.

## Verified docking and lifecycle continuation

The intended overlapping home-airlock/lifeboat connection in `docs/port-status.md` decision 55 remains. Duplicate lifeboat static walls and frame posts fully covered by host floor are suppressed on runtime instances, with supported-footprint sampling and deck-height checks. Only covered exterior host airlock walls yield to the mobile interior; host room/corridor boundaries, unsupported walls, other decks and locked/hatch blockers remain. Sources, prefab assets and docking transforms are unchanged. Instance colliders/renderers and adjusted interaction anchors restore on undock.

The host builds one navigation surface over the connected geometry; the mobile's separate surface is suppressed while docked and restored afterward, including protection against LateUpdate re-adding duplicate data. Door carving has no stationary delay. Repair/fire/seal anchors embedded in composite docking walls move to nearby supported, capsule-clear positions within their interaction radius. Their system identity, resource/channel behavior and authored source positions remain intact.

Authored home doors are usable. Closed doors precede nearby deck/station interactions; open doors yield to reachable ordinary targets, including deck transfers. The HUD and dispatcher use the same portal target and deck reach rules. Home open/unlocked door state now survives world and manual saves through an optional backward-compatible snapshot field and is restored on return.

Validation on this continuation:

* Core: **710 passed, zero failed**, `builds/logs/lifecycle-core.trx`.
* Unity Edit Mode: **919 passed, zero failed**, `builds/logs/lifecycle-editmode.xml`, including unsupported/deck/lock preservation, undock restoration and safe repair-anchor assertions.
* GPU Unity Play Mode: **50 passed, zero failed**, `builds/logs/lifecycle-playmode.xml`. The continuous walking test uses title/New Run, physical movement, real doors, lower/upper/lower/upper deck transfers, authored loot, save/Continue with opened-door assertions, objective interaction through extraction/results and fresh restart. It does not teleport between objectives or call EndRun/CompleteObjectiveSequence. The death test injects an encounter, then relies on ordinary enemy damage, results and restart; it does not force zero health or EndRun.
* Repair/travel/return and wound/chart persistence fixtures retain their channel completion, consumed resources and travel/save assertions. These are separate fixtures, not a claim of one continuous natural first-away expedition.
* Rebuilt Windows dev player: **passed**, Mono, 486.5 MB, zero errors, `builds/logs/build-StandaloneWindows64-dev.log`. Runtime gameplay is verified in GPU Play Mode; standalone verification is a boot-to-title smoke check, not a manual expedition.
* Actual gameplay-camera capture from the bidirectional traversal: `artifacts/screenshots/natural-hub-upper-deck.png`. Camera captures omit the UI; dim lighting, primitive markers and visual readability remain presentation work.

Intermediate failures uncovered real overlapping collision, missing home-door persistence and wall-embedded interaction anchors. The test steering was also corrected to follow intermediate corners and approach props within physical reach. Assertions were retained and expanded.

## Bounded next integration and mechanic scope

Companion revision `01230d84c895c13fdb744df98d5b39724c35ba44` exposes `ICreatureVisualFactory`, seeded pool generation and saved-recipe reconstruction. The next adapter should use production validation (`allowReview=false`), persist each organic threat's recipe, preserve gameplay collider/navigation authority and fall back safely when no approved skeleton/pool is available. Keep `drone_swarm` mechanical. No draft asset is approved or production generation enabled by this continuation; the game still uses its existing primitive threat factory. Package installation, adapter implementation and successful approved-creature rendering remain unfinished.

A subsequent small colony slice can build on the existing `biomatter_tangle` junk item (yielding `biomatter_residue` and `reactive_gel`), work channels, saved per-ship infestation state and deterministic threat spawning. Define explicit colony biomass sources, a persistent reserve and bounded defense spending/alert thresholds before adding gameplay. Preserve the Milestone A progression guards. This would be scripted resource/defense behavior; force/mass/energy-constrained adaptation and learning remain separate unimplemented systems. The ownership and depletion of colony biomass sources need to be settled in the mechanic design before that larger change.

## Third slice: live Critter production adapter

The live threat view now uses the companion's production validation and saved-
recipe factory interface for `biomatter_swarm`, `puppet_corpse` and `stalker`.
Mechanical `drone_swarm`, hallucinations and unmapped archetypes keep game visuals.
Optional threat summaries persist exact recipe JSON and a deterministic decimal-
string seed, with pool and library identity. Invalid, missing, unapproved or
unavailable recipes keep playable placeholders without load-time rerolls.
Game collision/navigation and direct `Mesh` feedback remain authoritative; visual
root motion and imported colliders do not move or block threats. Successful
creatures use the published per-build base speed and existing AI multipliers.

The embedded companion package is pinned to `01230d84c895c13fdb744df98d5b39724c35ba44`.
No owner asset approvals or real art imports were performed. A synthetic approved
skinned-mesh fixture verifies successful assembly and restoration. Actual local
draft reviews were found in `D:\critter-creator`; the minimum candidate review
and precise runtime setup are in [critter-runtime-integration.md](critter-runtime-integration.md).

Final adapter checks: Core **710 passed, 0 failed**; Unity Edit Mode **952 passed,
0 failed, 17 skipped** out of 969; GPU Play Mode **50 passed, 0 failed**. Nine
adapter regressions pass within the Edit Mode aggregate. The 17 skips are upstream
tests requiring a built owner library/export fixtures at the companion test paths:
15 LibraryTests and 2 PrototypeImportTests. They are not production rendering passes.
Evidence: `builds/logs/critter-core.trx`, `critter-editmode.xml`, `critter-playmode.xml`.
Windows development Mono rebuild passed: **487.6 MB, 0 errors**, build version
0.1.0 (`builds/logs/critter-build.log`). Production-owner rendering remains gated
by approval, independently of the successful build and synthetic fixture.
The rebuilt standalone reached the title menu successfully in a hidden headless
boot smoke check (`builds/logs/critter-player-smoke.log`); this is not a manual
standalone expedition or owner-creature rendering verification.

The next proposed finite-harvest/biomass-reserve/alert-defense slice is specified in
[colony-defense-proposal.md](colony-defense-proposal.md). It has not been implemented.

## Fourth slice: natural first-away progression

The default Engineer previously could not repair the lifeboat through ordinary
play: the hub maintenance cache lacked required parts, and crafting a reactor
core required unavailable inputs and fabrication skill. `start_supply_a` now
contains a finite, authored repair kit: three circuit boards, three power cells,
one data core, two sensor modules, one reactor core, a welder, a plasma cutter,
six hull sealants and a fire extinguisher. Its existing location and one-search
semantics remain. The other cache remains randomized. Existing saves that have
already searched this cache do not receive replacement items; start a New Run
to exercise the corrected onboarding supplies.

Real repair tools, materials, channel durations and skill checks remain. The
Engineer starts with repair skill three and earns the higher requirement by
performing available lower-skill repairs. Shared repair stations now expose an
actionable repair or unsearched supply before a blocked high-skill repair;
blocked feedback remains when no actionable target exists. An unavailable locked
door also yields interaction focus to an ordinary reachable target, without
unlocking or opening the door. Presentation and dispatch use these same rules.

The hub oxygen pump now uses its existing maintenance room when the authored
tool-storage room is absent, rather than an arbitrary player-relative offset
inside the overlapping dock. The fallback applies only to the supported hub.
Docked bridge/repair anchors require a supported standing capsule and a complete
navigation path from the host entry; relocation stays within original reach and
restores on undock. Deck-transition cues retain solid geometry but leave standing
passages beside their adjoining door frames. Authored sources, assets, docking
transforms and lock checks remain intact.

`WalkRepairTravelBoardAndReturnWithoutFixtureResources` starts through the real
title/New Run flow, physically walks between decks and loot, acquires the pump,
performs timed repairs and earns skill, seals breaches, walks to the cockpit,
uses guarded scanner travel, boards the generated wreck, searches loot, walks
back, returns home, saves and continues. It asserts persisted repaired systems,
earned skill, the pump, the searched one-time home cache and away loot identity.
It does not grant inventory, boost skills, teleport or complete objectives by
fixture shortcuts. Natural death and objective extraction/results/restart remain
separate acceptance tests, not a claim of one combined combat expedition.

The historical loader snapshots still compare every field. Their one intentional
cache change is pinned to the exact nine item/quantity pairs, not excluded from
comparison. Regression coverage also preserves blocked repair and locked-door
feedback and deck passage width across ten layouts.

For a local player check, open Boot and select New Run with default Engineer,
seed 17, breach field and standard difficulty. Use E at deck cues and doors,
search the upper-deck maintenance supplies, collect the oxygen pump, then return
to the lifeboat. Repair available lower-skill components before the reactor and
scanner assembly; use the actual timed repair channels and seal open breaches.
Walk to the cockpit, scan/select a reachable wreck, board and loot, then return
to the cockpit and home. Save and Continue to inspect retained progress.
Completing every hub objective intentionally ends the extraction slice; leave
that chain unfinished while checking first-away travel.

Actual gameplay-camera output is saved locally at
`artifacts/screenshots/natural-first-away.png`. It omits the HUD and shows the
current prototype presentation; it is not generated art or evidence of an
approved production creature. Default Engineer/seed 17 is the combined journey
covered here; other class onboarding, broad seed acceptance and combined
first-away combat remain further work. Colony mechanics and owner approvals
remain pending their separate decisions.

Final fourth-slice aggregates: Core **714 passed, zero failed**
(`builds/logs/first-away-final-core.trx`); Unity Edit Mode **966 passed, zero
failed, 17 existing upstream skips** out of 983
(`first-away-final-editmode-v2.xml`); GPU Unity Play Mode **51 passed, zero
failed or skipped** (`first-away-final-playmode.xml`). The combined first-away
journey and its final save/Continue assertions pass in that Play Mode aggregate.
The 17 Edit Mode skips retain the companion library/export-fixture requirements
documented above. Intermediate failing logs remain for diagnosis; they are not
the final result.

The final Windows development Mono player rebuilt successfully: **487.6 MB,
zero errors**, version 0.1.0 (`first-away-final-build.log`). A hidden headless
smoke launch reported the expected dev build stamp and reached the title without
errors (`first-away-final-player-smoke.log`). The expedition itself was verified
in GPU Unity Play Mode; standalone verification is limited to boot/title.

The capture retains prototype visual weaknesses, including overlapping docking
presentation, dim areas and primitive markers. Physical navigation/interaction
acceptance does not establish production visual quality or full-game completion.

## Bounded closing regression pass

The combined default first-away acceptance now also defeats an actual generated
encounter through normal reach/facing/LOS/cooldown attacks, survives the fight,
returns home, saves/continues and revisits the saved wreck to assert the defeated
enemy does not respawn. Dead threats are intentionally swept from live/saved
runtime lists; this test verifies removal and actual restoration rather than
requiring a dead threat record. It injects no enemies, damage or inventory.

Combat acquisition exposed a separate gap: crowbar fallback attacks still
require an equipped weapon, but crowbars appeared in no loot table. One existing
crowbar now supplements the finite maintenance cache and auto-equips through
normal loot handling into an empty hand. The final cache therefore has ten exact
stacks, superseding the fourth-slice nine-stack inventory above. Weapon damage,
enemy tuning, range, cooldowns and class balance remain unchanged.

The complete class/seed coverage table, limits and remaining decisions are in
[milestone-a-closing-matrix.md](playtest/milestone-a-closing-matrix.md). Eight
initial classes are checked through resource/skill-gated repairs and the partial
hub objective route; all retain hub extraction. Only Engineer and Mechanic
prepare all travel systems in the fresh-run matrix. Six classes need an intended
nontechnical progression decision if fresh-run first-away travel is required.
The default Engineer has physical Unity combat/return/save/revisit coverage;
the class matrix is Core contract coverage, not eight physical expeditions.
All 18 preferred-seed/size/condition candidates retain complete contract gating.
Seed 777 supplies no required hazard in that matrix and is not a working
guaranteed fallback. Colony scope and owner asset approvals remain pending.

| Fresh starting class | Starting repair | Repair route earned level | Partial-hub route earned level | Travel systems ready | Hub extraction contract |
|---|---:|---:|---:|---|---|
| Engineer | 3 | 5 | 4 | Yes, both routes | Pass |
| Mechanic | 4 | 5 | 5 | Yes, both routes | Pass |
| Medic | 1 | 1 | 1 | Blocked | Pass |
| Pilot | 1 | 1 | 1 | Blocked | Pass |
| Scientist | 2 | 3 | 2 | Blocked | Pass |
| Cook | 0 | 0 | 0 | Blocked | Pass |
| Security | 1 | 1 | 1 | Blocked | Pass |
| Communications | 0 | 0 | 0 | Blocked | Pass |

The six blocked travel paths are diagnosed limitations, not successful physical
expeditions. Class starting skills, multipliers, finite resources and real skill
gates remain unchanged. The decisions are: whether these classes are intended
to remain hub-extraction-only in Milestone A or need a designed nontechnical
first-away route; whether to commission a guaranteed alternate first-away
candidate instead of retaining the current seed/shape exclusions; colony scope;
and owner approval of the draft crawler. No new feature or challenge rebalance
has been implemented to conceal those decisions.

Closing aggregate evidence: Core **732 passed, zero failed**
(`builds/logs/closing-final-core-v3.trx`); Unity Edit Mode **984 passed, zero
failed, 17 unchanged upstream fixture skips** out of 1001
(`closing-final-editmode-v2.xml`); GPU Play Mode **51 passed, zero failed or
skipped** (`closing-final-playmode.xml`). The combined generated encounter,
return, Save/Continue and no-respawn revisit assertions pass within that final
aggregate. Earlier diagnostic assertion failures are retained in intermediate
logs, not counted as final passes. The class tests explicitly expose blocked
first-away paths; their passing assertions do not make those paths playable.

The final changed-data Windows development Mono player rebuilt successfully:
**487.6 MB, zero errors**, version 0.1.0 (`closing-final-build.log`). Expedition
behavior is covered by Editor GPU Play Mode; standalone validation remains
limited to a separate boot/title smoke check.

That final player smoke check passed with the expected dev build stamp and
`[TitleScreen] ready`, with no detected exceptions/errors or missing scripts
(`builds/logs/closing-final-player-smoke.log`). No launched verification process
is left running.

## Windows New Run shader failure (2026-09-30)

The owner reported the development executable staying on the New Run overlay.
The preserved player stack (`builds/logs/user-reported-shader-failure.log`)
confirms `RuntimeVisualCatalog.Material` threw while creating objective volumes:
`Universal Render Pipeline/Unlit` was absent. The previous title-only player
smoke check did not exercise this path and did not establish player playability.

The catalog used `Shader.Find`, which does not declare a shader dependency for
the player build. Unity documents this Editor-versus-player limitation in its
[Shader.Find reference](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Shader.Find.html).
The corrected catalog clones six serialized Resources material templates:
opaque/transparent Unlit and opaque/transparent Lit with and without emission.
This retains both URP shaders and the actual feature keyword combinations used
by generated visuals, without substituting another shader or including every
URP variant. Double-sided culling and per-object colors/emission remain runtime
properties. The URP importer requires emissive template GI flags to retain the
emission keyword; runtime clones retain the existing emission/GI behavior.

`RuntimeVisualMaterialBuildCheck` validates the library and template keywords
for every build, including manual builds. To deliberately regenerate authored
templates, use **Synaptic Sea > Build > Rebuild Runtime Visual Materials**.
Eight Edit Mode checks cover build dependencies, every template, runtime clone
properties/cache identity, and incomplete/wrong-keyword rejection.

The corrected development Mono output is in a distinct folder:
`builds/StandaloneWindows64/shader-fix-20260930/TheSynapticSea.exe`.
Its stamp is **2026-09-30T15:37:04Z**, version **0.1.0**, Unity **6000.6.0f1**;
the build passed with **488.3 MB and zero errors** (`shader-final-build.log`).
`TheSynapticSea.exe` itself is Unity's launcher: distinguish builds with the
stamp and runtime assembly, not just the launcher's unchanged file hash.
Runtime assembly SHA-256:
`50F6924B85E55BD0D080B4F68EF5B1A227107221E1EF070998EBBD76E7AF2FB4`.
The older `dev` output has been preserved and still contains the reported bug.

Current full Edit Mode evidence: **992 passed, zero failed, 17 unchanged fixture
skips** (`shader-final-edit.xml`). A separate GPU **WindowsPlayer** test run
passed **2/2**, zero failures/skips (`shader-player-regression.xml`, build/run
trace `shader-player-regression.log`): Title/New Run creates the real Playable
scene and returns to Title, and all six shader configurations are supported
with green opaque/alpha Unlit render/readback and real alpha blending against
black. The bootstrap test uses UI command dispatch; the material probe uses
actual GPU rendering in a separately built test executable. Neither is native
OS input validation of the shipping development executable.

The final affected **Editor GPU Play Mode aggregate passed 52/52**, zero
failures or skips (`shader-final-play.xml`), including the existing natural
first-away/combat/return/Save/Continue journeys and the new shader rendering
regression. Core was unchanged by this slice; its previous 732-pass result is
not described as a newly repeated run.

Agent-driven native testing is currently blocked by foreground access: the active VMware
Horizon client retains the foreground even when normal Windows game activation
is requested. No keyboard input is sent to an unverified foreground window.
The owner must pause that session and foreground **The Synaptic Sea** from the
distinct corrected folder before native Start Run/movement/save verification.
Owner startup/movement/interaction acceptance is recorded below; no successful
agent-driven native-input journey or new native screenshot is claimed yet.
Existing saves were backed up locally before testing; original repositories,
assets, approvals and colony design choices remain untouched.

The owner subsequently launched the distinct corrected development executable
and confirmed: the game loaded, movement worked, and objects could be
interacted with. Its actual player log (`shader-user-corrected-run.log`, process
54556, observed at the corrected executable path) records a successful New Run
bootstrap with seed 17, breach_field, standard, Engineer and no matching
exception/error lines. This is owner-operated native play evidence; the agent
did not send those inputs. Native agent-driven save/load and a native capture
are still unverified. The owner also identified excessive camera height and
poor in-room interaction visibility, which is the next separate fix.

## Camera and interaction readability — 2026-09-30

The owner's follow-up requested Project Zomboid-inspired framing, wheel zoom and wall hiding. The resulting camera uses a fixed approximately 30-degree diagonal orthographic view, default half-height 7 (14 metres of vertical view), with smooth bounded wheel levels 4 through 11. These are project tuning rather than documented PZ engine constants. Room scale, assets, collision and navigation are unchanged. A focused `E · <action>` world label follows the same authoritative interaction target. Menus and scrollable UI suppress camera zoom.

Parallel screen rays reveal all structural obstructions over the player and focused object. Docked union collision does not retain individual module owners, so actual structural visual bounds participate too. Supported serialized URP variants fade; unsupported custom/purchased shaders use reversible hiding and a thin floor footprint. Upper-deck blockers restore along with walls on movement, zoom, pooling, teleports and teardown. A retained depth-tested shader provides a player-only silhouette. Physical line of sight still suppresses enemies behind solid walls. Shared materials are preserved and temporary material clones are destroyed. This is a fade/hide implementation with boundary context, not PZ's identical curved ankle-height cutaway.

Final verification: **1,001 Edit Mode passes, zero failures, 17 unchanged upstream skips** (`camera-final-edit.xml`); **54 GPU Editor Play Mode passes**, zero failures/skips (`camera-final-play.xml`). The independently built GPU Windows test player passed **5/5**, zero failures/skips (`camera-final-windows-local.xml`): Title/New Run, wheel/focus, actual room rendering at multiple zoom/deck positions, F5/F9 save/load and retained shader support. Its supported NUnit callback saved results locally and the player exited. The initial Editor-connection launch failed with no callbacks and is not counted as a test pass. No native OS input was sent. Core code is unchanged; the previous 732-pass result was not rerun for this presentation slice.

The separate development Mono build passed with **488.3 MB, zero errors**, timestamp **2026-09-30T17:05:02Z**, Unity **6000.6.0f1**, version **0.1.0**. Open:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\camera-zoom-20260930\TheSynapticSea.exe`

Runtime assembly SHA-256:
`0397328BDA7FC747DEC4A435811B3E9A1C182259C4FA8B87E87707BB3DA8B86A`.

The working shader-fix build remains preserved. The owner should close their older game when convenient before launching this distinct camera build. The owner already accepted loading, movement and interaction on the shader-fix build; owner acceptance of these new camera controls and agent-native input remain pending. Existing saves and licensed source assets are untouched. Colony mechanics, class progression decisions and creature owner approvals remain paused.

Actual 2048 x 1224 Windows GPU camera renders are in `builds/StandaloneWindows64/artifacts/screenshots/camera-{before,after,close,far,upper-deck}.png`. The comparison confirms larger indoor framing and obstruction reveal with unchanged structural transforms/collider states. These are real camera-only captures, not generated images or native desktop/HUD screenshots. Existing dark lighting and placeholder art remain; no broad lighting redesign, exhaustive material/performance benchmark or full-game completion is claimed. Detailed controls, restoration coverage, limitations and Unity opening instructions are in [camera-readability.md](playtest/camera-readability.md).

## Procedural expedition service-loop slice — 2026-09-30

The training hub and first-away onboarding remain intentionally bounded. The live generator's template zone counts ignored the blueprint room budget, and disconnected loop endpoints had no corridor routing. The first larger expedition profile now creates physical parallel service routes, alternate sector entrances and side pockets, with deterministic sector widths/depths, pocket positions and orientation. New size-1/2 wrecks after the first visit use saved `service_loop_v1` identity; old visited ships and small boats retain their legacy geometry. Functional roles and existing variants/encounters/condition overlays remain active. This is one expanded topology family, not the completed procedural redesign.

Actual local corner/T-junction visuals were 2 m high while straight walls were 3.5 m high. The kit adapter now retains full-height canonical edges when short junctions cannot replace them. Whole selected logical wall modules reveal together, including off-ray sibling trims, and compatible materials fade fully away. Door assemblies and the intentional boundary footprints remain separate geometry; this does not claim every possible module composition has been visually certified. Original meshes, damaged/breached variants, collision and enemy physical perception are preserved.

Final verification: **741 Core passes**, **1,011 Unity Edit Mode passes with 17 unchanged upstream skips**, **55 GPU Editor Play Mode passes**, and **5 independent GPU Windows-player passes**, all zero failures. Logs are `expedition-final-core-v2.trx`, `expedition-final-edit-v2.xml`, `expedition-final-play.xml` and `expedition-windows-local.xml` under `builds/logs`. The new seed-42 size-2 scene has **24 rooms, 138 floor cells**; every floor cell has a complete NavMesh path from its start with fixture doors opened to isolate geometry. Existing natural onboarding/combat/return/save tests also pass. A natural native-keyboard expedition journey in the new profile has not been performed; no foreground input was sent to the owner's active session.

The distinct development Windows Mono build passed, **488.3 MB, zero build errors**, Unity **6000.6.0f1**, version **0.1.0**, built **2026-09-30T17:59:20Z**:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\procgen-expeditions-20260930\TheSynapticSea.exe`

Runtime assembly SHA-256: `5AB4BD4B658BE04D13192B7BF0549AEDD2553ABED20F876263BDEA644A3F118F`. The build stamp's Git SHA is empty; use this timestamp/hash to distinguish it from preserved camera and shader-fix builds.

To see the expansion, finish the established first-away return, then choose a previously unvisited size-1 or size-2 wreck on the scanner. Do not change the guarded New Run setup. Existing visited wrecks are not upgraded in place. Close the older player when convenient before starting this distinct build; saves and source assets are preserved.

Actual Windows GPU camera-only captures, locally reviewed at 2048 x 1224, are `builds/StandaloneWindows64/artifacts/screenshots/expedition-overview-seed42.png` and `expedition-dock-seed42.png`, with refreshed wall/upper-deck views beside them. The overview confirms side pockets and service routes; the dock close-up shows full-height boundary joins. These are not native desktop/HUD captures and have not been uploaded to Library. Library audit version 6 predates this section.

The measured Windows generation/scene/NavMesh cost was **780 ms**, with **6,370 renderers**. This is load-cost evidence, not a steady frame-rate guarantee. Detailed modules, lights and occlusion scans need profiling before further size inflation. Interiors still need richer purpose-specific furnishing; multiple hull families, routed shortcuts and usable multi-deck transitions are subsequent stages. See [the procedural expedition review](playtest/procedural-expeditions.md) for the compatibility boundary, acceptance evidence and staged scope. Colony mechanics, class decisions and creature approval remain paused.

Additional delivery verification: the extended natural walking/repair/combat/return/Continue journey passed and reached a normal scanner contact (`-1:0:0`, size 1, 17 rooms) as its second new destination, then restored that destination/profile through Continue (`expedition-natural-route.xml`). One successful first-away visit unlocks the profile. After returning, board the repaired lifeboat, stand near its bridge terminal, press **Tab**, choose an **unvisited size-1 or size-2 contact**, and select **Travel**. No further trips or extraction are required. New Run and first-away geometry remain unchanged; old visited wrecks remain unchanged. Existing saves with a visited wreck can take a new larger contact immediately.

The additional Windows rendering/occlusion profile passed. On the RTX 4070 Laptop GPU at 2048 x 1224, GPU-fenced elapsed medians / 95th percentiles were **6.05 / 7.63 ms indoor** and **5.68 / 6.68 ms overview**, with approximately **3.4 ms occlusion CPU**. Each view measured 120 frames after 30 warm-up frames. This bounded fixture showed no rendering/cutaway playability bottleneck on Deviltop; live HUD/combat AI and slower hardware remain outside that measurement. Direct GPU timing was unavailable, so the result includes render completion/readback synchronization and scheduling rather than claiming GPU-only or full-game FPS. See the procedural review for methodology. These closing changes affect tests/documentation only; the delivered development runtime hash is unchanged.

## Starter landing and cutaway repair - 2026-09-30

The reported character-under-elevator screenshot was inspected and reproduced in the actual starter scene. The character was correctly standing on deck 0 (feet about 0.14 m), beneath a legitimate deck-1 platform around 4.1 m. Camera reveal hid that upper floor but recreated its obstruction as gray upper-floor footprints. Per-renderer wall footprints also left floating strips at trim heights. Floors/ceilings and other-deck wall bases now leave no footprint; each revealed wall/door assembly retains at most one subtle boundary at its base. Imported collision-only helper meshes no longer render. Physical floor, wall, door and hazard collision and enemy perception are preserved.

Presentation trigger volumes and duplicate landmark/blockage cubes no longer render as room furniture. Breach/arc/route warnings use thin floor overlays; actionable semantic props and real triggers remain. World labels respect physical walls and deck height, with focused interaction feedback retained. The fallback environment for layouts without authored lighting now includes readable ambient and metal reflection, while legacy comparison calibration and authored biome lighting remain intact. This preserves the starter layout; it is not the pending hub expansion or next topology family.

Final verification: **741 Core passes**, **1,017 Edit Mode passes / 17 unchanged existing skips**, **56 GPU Editor Play Mode passes**, and **1 targeted independent Windows GPU journey pass**, zero failures. The journey uses Title/New Run and real walking, both deck transfers and save/Continue at both landings. Windows captures show the actual rendered camera and HUD at default/close/far zoom and saved upper/lower landings. Black batch screen captures were rejected; the verified capture renders the actual camera and HUD panel into textures and composites those rendered pixels. No native desktop input or owner-save mutation occurred. Logs and limitations are in [ramp-presentation.md](playtest/ramp-presentation.md).

The separate development Windows Mono build passed: **489.8 MB, zero errors**, Unity **6000.6.0f1**, built **2026-09-30T19:43:24Z**, runtime fix commit `aa03746a6d8421bb905b40d161015cd78a86def6`:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\ramp-readability-20260930\TheSynapticSea.exe`

Runtime assembly SHA-256: `89C659C61B0F076A2BAE7ED119200E309C7A090CD62DE3FD4AF4BE14F3027488`. The build stamp's Git SHA is empty; distinguish this build by folder, timestamp and runtime hash. Close the older game when convenient before launching it. Continue preserves the existing world; New Run retains the guarded default setup. Scroll zoom and **E** deck interactions are unchanged. Automated Windows gameplay used the independent test player built from the same final runtime source; native input in this development executable remains unverified while the owner uses the desktop.

Actual 2048 x 1224 world/HUD captures remain local under `builds/StandaloneWindows64/artifacts/ramp-review/`. No new Library upload occurred, and the earlier Library audit does not contain this repair. Placeholder semantic props and unfinished art remain visible; this is not complete scene polish or a claim that every composition is certified.

## Purposeful expedition v2 - 2026-10-01

New eligible later expeditions use saved `purposeful_expedition_v2` identity. The cargo-exchange family has a broad cargo hall, radial routes and distinct crew, medical, engineering, bridge and maintenance wings. The service family retains parallel routes and seed-dependent side pockets. Cargo footprint and rotation vary with the seed. Perimeter compositions use the existing local visual bindings for pallets, carts, cabinets, lockers, benches and racks, plus simple primitive beds/cots. Generic clutter is replaced in furnished rooms, with door and work/loot approach cells reserved. These are two physically different hull recipes and a room-composition foundation, not a general space solver or completed procedural game.

Real traversal uncovered docking issues beyond fixture connectivity. A dedicated dock had been excluded from supported wall admission, its centroid misaligned the boat across wall modules, and its facing ignored hull rotation. V2 now carries an explicit outward-facing port on a real outer floor cell. Host interior boundaries, locks and unsupported edges remain intact. Restored mobile control anchors also recheck settled navigation from the actual player's side of the overlap, within real reach, and restore their original positions when undocked. Legacy/v1 saved geometry and port derivation remain on their original paths.

The starter hub and first-away contract remain unchanged. After the first-away round trip, stand at the repaired boat bridge, press **Tab**, and travel to a new size-1/2 contact. Existing saves with a visited wreck can choose a new eligible contact immediately; returning to old visited contacts retains their saved profile. Both families use normal scanner selection and guarded travel. The remaining architecture, composition, pacing, multi-deck and measured-performance stages are described in [purposeful-expeditions.md](playtest/purposeful-expeditions.md). Medical/living loot still uses existing generic containers; furnishings are visual-only, encounters retain current rules, and production creature adoption is separate work. Pending hub, colony and class decisions are not implemented.

Long natural exploration exposed a separate authority defect: home web deterioration exhausted the home hull while power was fully allocated and the manager had no broken systems, yet that home hull penalty disabled the intact lifeboat. Propulsion now derives hull penalty and operational status from the piloted ship. A damaged piloted hull still gates departure. Home web growth, hull and atmosphere consequences are retained, including the observed exhausted home in this long journey; pressure pacing and recovery remain product work. The Milestone A onboarding power grid remains shared and the currently unconfigured lifeboat compartment hull follows the existing pristine-empty-hull contract. This does not implement independent vessel simulation or a new colony economy.

Final verification: **752 Core passes**, **1,029 Edit Mode passes / 17 unchanged existing skips**, and **60 GPU Editor Play Mode passes**, zero aggregate failures. Windows validated the cargo natural journey and three geometry/scene checks; the service natural journey passed its corrected self-defense rerun. Both use Title/New Run, finite onboarding resources, earned repair skill, real controller walking and door interaction, first-away combat, guarded second-trip selection, destination save/Continue, room exploration, return and final Continue. Earlier failures are retained and distinguished in [purposeful-expeditions.md](playtest/purposeful-expeditions.md), including the unsuccessful sequential three-trip radiation soak. No health, resource or skill grants and no challenge rebalance were added to the natural journeys.

Compiled exterior walls include an empty outside owner; supported dock admission now counts only real room owners. Fully admitted walls hide and restore the complete visual assembly, including high trim. Typed `Vec2i` and serialized portal cells use the canonical parser for dock facing and reserved approaches. Internal boundaries, locks and unsupported void retain collision. These changes and representative rendered scenes were verified; unfinished semantic props, warning overlays and art remain.

The separate **489.8 MB Windows Mono development build** passed with zero errors at **2026-10-01T02:50:13Z**, version **0.1.0+9fe8939**, source commit `9fe893907998fce66a6ace712451ec47e0cc1560`:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\purposeful-expeditions-20261001\TheSynapticSea.exe`

Runtime SHA-256: `78CF2D5E671CDE12062B0F1ECBC537DA7D84691D501870619CA00448BB8A320F`. For the default seed-17 world after one first-away round trip, choose a new scanner contact **`-1:0:0`** for the **17-room service hull** or **`-1:-1:1`** for the **11-room cargo hull**; either is accessible on the second outbound trip. Previously visited contacts keep their original profile. Other eligible new size-1/2 contacts vary by saved world. The starter and first-away scenes look unchanged.

Clean live-host bridge-view frame intervals on this RTX 4070 Laptop GPU at 2048 x 1224 measured median/p95 **17.15/23.25 ms** (service) and **17.92/25.12 ms** (cargo). Scene renderer-component counts were 7,784 and 8,215, including hidden components; these are not draw-call counts. This is bounded, view/device-specific offscreen evidence, not a native-present or low-end performance guarantee. Full-HUD and multi-seed captures remain local at the paths in the review; no new private Library upload occurred. Agent-native input in the new development executable remains unverified while the owner uses the desktop.

The next high-impact generator work is a versioned constrained room-graph/hull solver, validated room-composition catalogs, spatial hazard/recovery and exploration pacing, then real multi-deck circulation and measured rendering optimization. At this v2 checkpoint, generic medical/living loot, blanket away-radiation fallback and fast home pressure still limited prolonged expeditions; the later V4 recovery slice below addresses finite expedition supplies and authored zero-radiation handling. Starter expansion, colony scope and class balance remain pending decisions. Companion provenance/adoption is separate: the upstream crawler approval reported in PR #42 must be matched to the exact pinned local artifact before adoption; this slice neither requests renewed approval of that unchanged upstream artifact nor changes draft statuses or imports new companion revisions.

## Reclaimed home and recovery expeditions - 2026-10-01

Runtime commit `7fc29936597abaa2a20d6d9289f15ca31d2ca539` makes a suitable repaired later wreck usable as a secured home extension. It retains independent ship systems and propulsion ownership, finite repair/recovery supplies, existing tool/skill/material checks, actual hull sealing and fire suppression, qualified exterior mooring, interrupted/completed welding, and a closed connection door opened through normal interaction. Cargo-hold and bay controls share eligible nearest-target selection; opening cargo no longer unintentionally launches the docked shuttle. Physical connection collision and composite navigation survive integrity changes, active-context changes and save/Continue.

The complete fresh default Engineer journey passed in the final GPU Play aggregate: Title/New Run, onboarding, first-away combat/exploration/return, a newly generated recovery wreck, finite searched care/provisions/parts, six damaged-system repairs, real sealing and plating salvage, repaired-vessel travel home, material-gated welding, walking the connection both directions, Continue inside the extension, and independent-shuttle departure/return. No fixture inventory, health, skill or position grants are used. Final same-source totals are **786 Core passes**, **1,064 Unity Edit passes / 17 existing companion-library skips**, and **63 GPU Play passes**, with zero failures. Evidence: `home-extension-final-core.trx`, `reclaimed-home-final-edit.xml`, `reclaimed-home-final-play.xml` under `builds/logs/`.

For normal default-world access, use Engineer / standard / seed 17, earn the existing onboarding tools/training, cut home/shuttle moorings and complete one first-away round trip. On the second outbound trip choose new contact **`-2:0:0`**, the tested 13-room `reclamation_expedition_v4` wreck. Search medical, crew, maintenance and cargo rooms; suppress fires, recharge the finite extinguisher at powered equipment, repair local systems and seal breaches. Claim the working bridge, stow excess salvage through its cargo hold while keeping two plating, satisfy the mobility report and Return home. Hold the welding prompt with an existing welding tool and the plating, open the completed connection door normally, and walk either way. Previously visited V3 layouts and loot remain unchanged; an existing save that has already visited this contact needs another eligible unvisited contact and does not reproduce the exact default fixture. The starter layout and first-away guard are preserved.

This is a gameplay load/capability model, not Newtonian propulsion. Services are local; connections do not implement pressure/power conduits, and only one NPC context is active. Room air remains an initial-condition/local-recovery model rather than finite pressure simulation. Natural home-engine installation/fleet flight and all-class balance are not accepted by this one default journey; its survival margin is tight. Colony scope, starter expansion, class balance and exact companion-approval provenance remain separate pending work. Windows gameplay evidence and the distinct development player are recorded after their final checks below. See [reclaimed-home-extension.md](design/reclaimed-home-extension.md) for contracts and implementation limits.

Final independent Windows GPU verification passed **five cases, zero failures/skips**: Title/New Run/return to title, landing transfers/save, fresh reclamation/weld/two-way walking/Continue/independent-shuttle return (**531.12 s**), natural cargo-family exploration/combat/return/save (**178.37 s**), and retained shader rendering. The authoritative result is `builds/logs/reclaimed-home-20261001-windows-local.xml`; the quiet launcher editor did not receive remote callbacks. Earlier build-process compilation/permission failures were resolved before this successful player run. No failing gameplay assertion was removed.

The separate Windows Mono development build passed **488.4 MB / zero errors**, version **0.1.0+7fc2993**, Unity **6000.6.0f1**, built **2026-10-01T12:05:06Z**:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\reclaimed-home-20261001\TheSynapticSea.exe`

Runtime assembly SHA-256: `AD6425994337ADB0023A4FC3F282EA0218B86CE46F0EBD8560E0E510AD80044D`. Actual Windows camera/HUD captures remain local under `builds/StandaloneWindows64/artifacts/ramp-review/`, including `natural-reclaimed-passage.png` and saved lower/upper landing views. The scene still uses unfinished semantic models; this is functional-route verification rather than complete scene-quality acceptance. No new Library upload or native desktop input occurred. The existing player and saves remain intact.

The joined three-member home view measured **14.26/18.20 ms median/p95 frame intervals** over 355 samples on the RTX 4070 Laptop GPU at 2048 x 1224, with normal host/survival/UI, 13 active-context rooms and zero active threats. The 9,149 renderer components include hidden components and are not draw calls. Asynchronous GPU-readback completion latency was measured separately and is not a frame-rate metric. This single stationary offscreen result is not a general combat or low-end performance guarantee.

## Mobile-home transit and bounded acceptance - 2026-10-01

Commits `02bed29` and `3ef2343` preserve the secured home assembly's local geometry while discrete surveyed-contact travel saves its sea location. Travel retains independent member systems, requires local controls and aggregate propulsion capacity, and never docks or claims the destination hull. The independent shuttle can leave and return to that location. Optional versioned fields preserve older stationary-home saves and reject malformed coordinates before mutation. This is discrete travel, not continuous physics propulsion or a colony economy.

Existing consumed hatch-bypass tools now have earnable starter-workbench recipes with real materials, fabrication skill and work time. Rotated hatches use a visible-face interaction query and matching collider orientation/navigation carving; solid-wall combat/perception is retained. Specific work-denial/interruption causes now appear in the work HUD, including exhaustion, tool, skill, materials and leaving reach. Propulsion denial explains actual supported mass and assembled load. No survival rates, encounter ranges, rare propulsion rolls or class balance were changed.

The final runtime checks pass **796 Core** (`mobile-home-final-core.trx`) and **1,079 Unity Edit** tests (`assembly-utility-feedback-final-edit.xml`), zero failures with 17 unchanged companion-library skips. The bounded GPU Play rerun reports **63 passed / one failed** (`mobile-home-final-supported-play.xml`); the separately failing fresh propulsion journey is excluded, so this is not a full 65-case pass. Natural cargo-family exploration/combat/return/save passed in **180.67 seconds**. The natural reclamation controller reached its weld with depleted stamina, thirst reserve and food, then timed work blocked. Its assertions remain intact. Earlier passing natural reclamation evidence above belongs to the earlier build.

The installed-assembly subsystem fixture passed in **91.59 seconds**, proving material-consuming interrupted/completed weld, closed/open door behavior, real two-way walking and ramp transfer, timed installation, assembly transit, Continue and independent shuttle departure/return. It explicitly replenishes vitals and supplies missing installation parts; it does **not** prove natural acquisition or a fully successful fresh extended route. The checkpoint and fixture captures remain local. Full fresh earned propulsion acceptance needs further manual/controller verification rather than more unbounded automated survival retries. See [mobile-home-transit.md](design/mobile-home-transit.md) for routes, fixture policy and exact remaining limits.

The separate Windows Mono development build passed **489.9 MB / zero errors**, version **0.1.0+dc2a187**, Unity **6000.6.0f1**, stamped **2026-10-01T17:10:12Z**:

`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\mobile-home-20261001\TheSynapticSea.exe`

Runtime assembly SHA-256: `2F29230D7941B793B9C2A13F4514F8C7149E83BCA958DB2B04C323C26E6BF5B3`; Core assembly SHA-256: `9E6A5E70A46C03AAF055D6DE851E26A5D4FA0C4D0B53EC7CFD567E29DA8D8E1A`. The same-runtime-source GPU Windows test player passed **five cases, zero failures/skips** (`mobile-home-20261001-windows-local.xml`, authoritative local report): installed-assembly fixture **94.07 s**, Title/New Run **0.27 s**, both ramp transfers **7.01 s**, natural cargo-family journey **178.67 s**, retained shader variants **0.01 s**. Initial test-build compilation/write failures were resolved by backing up/regenerating only Unity's generated Bee response cache; both failed build logs are retained. The editor launcher lacks the remote callback, so its exit alone is not the gameplay result. Native OS input in the development executable and natural propulsion acquisition remain unverified. Existing player saves and the previously delivered executable are preserved.

Actual GPU Windows camera plus full-HUD fixture captures remain local under `builds/StandaloneWindows64/artifacts/ramp-review/`: `installed-connection-fixture.png` and `installed-home-transit-fixture.png` (2048 x 1224). They were visually inspected and are explicitly fixture/offscreen output, not native desktop screenshots. The primitive player model remains unfinished; the transit capture also shows a low-oxygen hint alongside 100% suit oxygen, an unresolved hint-presentation discrepancy. No new private Library upload occurred.

Open the distinct executable when finished with the current game, then **Continue** to preserve the saved world. Default New Run remains Engineer / standard / breach_field / seed 17 with unchanged starter/first-away guards. After a first-away round trip, new contact `-1:-1:1` exposes the cargo family and `-1:0:0` the service family; visited hulls retain their saved versions. Contact `-2:0:0` supports the existing recovery/reclamation route. A secured home extension exposes home controls, but moving the assembled home still requires an owned installation, repair 4, a qualifying welder, one nozzle, one fuel line, one circuit board and sufficient supported mass. Crafting hatch tools at the existing home workbench does not supply those engine parts or bypass the installation work. The fully earned extended flight route remains unaccepted; use the earlier delivered executable as the reference for its already verified natural reclamation path.
