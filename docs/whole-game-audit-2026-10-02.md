# Whole-game integration audit — 2 October 2026

> **PRELIMINARY architectural pass, not a completed whole-game audit.** The deeper evidence and corrective findings are in [the deep audit](audit-deep/README.md), including full content registers, class producer analysis, current-Core diagnostics and fresh Unity fixtures. Those findings supersede unsupported broad absence/deadlock claims and priorities below. Cargo sealing supplies repair XP omitted from early class analysis. EVA tether physics, Newtonian flight, multiplayer, NPC crews, free-form cutting and detailed collector ecology are optional requirements; biomass resource rules require a bounded decision. No completion percentage is implied. Historical test/build statements below describe this first pass only, not the subsequent deeper diagnostics.

## Scope and conclusion

This audit evaluates the game against persistent, long-haul space-horror survival: scavenging and repairing vessels, welding useful wrecks into homes, maintaining those homes, surviving biomass creatures, and relocating an owned vessel or assembly when its propulsion supports its load. Small reconnaissance/scavenging craft retain a role. Completing onboarding is not an extraction victory.

The game contains substantial working short-session foundations. Movement, interaction, crafting, repairs, survival attrition, combat, room-aware threat perception, generated expeditions, docking, bounded structural work, secured home connections, owned assembly mobility, and versioned persistence exist. The largest gap is the integration of those foundations into a conserved, persistent world that supports repeated maintenance, expansion, relocation and recovery over weeks. Neither the number of classes nor passing unit tests measures completion of that experience.

No gameplay source, assets, approvals or saves were changed for this audit. No new test, build, pull, commit or upload was performed. This document is the only new deliverable.

## Versions, method and evidence limits

- Game: `F:\tmp\synaptic-sea-playable`, branch `codex/playable-combat-fixes`, commit `840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48`.
- Companion: `D:\critter-creator`, local commit `00735856e4d06e5823608e0ab77832a992baf098`.
- Embedded companion package: `01230d84c895c13fdb744df98d5b39724c35ba44`, package 0.2.0, schema/generator v3. See `SynapticSea/Packages/com.ninthlevelsoftware.crittercrafter/UPSTREAM.md`.
- Existing development build: `builds/StandaloneWindows64/mobile-home-20261001/TheSynapticSea.exe`, Unity 6000.6.0f1/Mono, version `0.1.0+dc2a187`, built 1 October 2026 at 17:10:12 UTC. The final documentation commit is newer than the runtime build.

Evidence is classified as **runtime-observed**, **source-confirmed**, **static integration risk**, **unverified**, or **design decision**. Runtime observations below come from retained logs and captures, not a new audit run. A failed controller journey is not automatically a product defect. A fixture with granted resources is not an earned progression proof. A model method is not evidence that normal play invokes it repeatedly.

Deep tracing covered RunSession bootstrap/tick/travel/save, onboarding, generated layouts, home connections, assembly capability, component work, survival/production, threat perception/combat, and the live creature adapter. Presentation, accessibility, audio, asset normalization and catalogs were sampled. Every proprietary pack, every item/recipe, every class/configuration, native desktop input, multi-week play, low-end performance, and all imported upstream tests were not exhaustively inspected. Existing documents include historical claims; Godot inventory percentages and old “next slice” notes are not present Unity integration evidence.

## Recorded verification and current liveness

| Evidence | Result | Practical limit |
|---|---|---|
| Core, `mobile-home-final-core.trx` | 796 passed | Pure-model contracts, not a full player journey |
| Edit Mode, `assembly-utility-feedback-final-edit.xml` | 1,079 passed; 17 unchanged companion skips | Imported fixture skips; not newly failing game tests |
| GPU Play Mode, `mobile-home-final-supported-play.xml` | 63 passed; 1 failed; 64 selected | Fresh extended-propulsion case excluded; do not call this a full 65-case pass |
| Natural cargo-family journey | Passed, about 181 seconds in Editor and 179 seconds in Windows | Bounded expedition/return/save coverage, not weeks of sustainability |
| Natural reclamation journey in final selection | Failed before first weld assertion | Stamina about 0.83, thirst 0, hunger 29; held work began then exhausted. Controller/resource budgeting failure, not proof every player is blocked |
| Separate fresh extended-flight journey | Failed, about 206 seconds | Death before reaching medical supplies during cargo sealing; combat 72, atmosphere 10.686, fire 2.131, wounds 15.224 damage. Two untreated severity-0.9 lacerations; not a pure oxygen death |
| Windows GPU test player, `mobile-home-20261001-windows-local.xml` | 5/5 passed, no skips | Scripted controller/UI input, not native OS keyboard/mouse |
| Installed-home Windows fixture | Passed, about 94 seconds | Starts from an earned moored checkpoint but replenishes vitals and grants missing installation parts |
| Development build | Succeeded, zero build errors | Build success alone does not prove native-input or sustained play |

The installed-home fixture verifies interrupted/completed welds, two plates, door open/close, physical walking both ways, ramps, timed installation, assembly transit, save/Continue and shuttle departure/return. It does not establish natural engine-part acquisition or sustainable operation. Saves in those tests use MemoryStorage; disk interruption safety is a separate concern.

At the audit checkpoint no test player or dotnet runner was active. The only Unity process was PID 29812, the separate original-project wrapper started 30 September; it was left untouched. Final logs had completed results, not an indefinitely running journey.

Earlier reclamation-build passes belong to older code and are historical evidence only. Existing offscreen full-HUD captures are actual rendering, not generated images. They are fixture captures and do not establish native human play or finished art quality. A retained transit capture showed a Low Oxygen hint with Suit O2 100; its cause has not been reproduced in this audit.

## Architecture and authority

`Core` holds engine-independent models, catalogs and services; `Runtime` connects them to Unity objects/input; UI presenters expose state and commands. RunSession coordinates many partial domains. The Variant/Godot compatibility layer is substantial working port infrastructure, not proof that all former Godot behavior remains meaningful in Unity.

`Session/TickStages.cs:139–153` explicitly orders home and away stages. Both include wounds, survival, food, work and threats, but ordering differs. `RunSession.Tick.cs:128–145` advances known ships with valid scene roots, not merely the active vessel. `RunSession.Ships.cs:42–64` creates ShipRuntime adapters and catches up absent ships. Expanded power/life-support/station coordination remains centered on the home. Active interaction, fire, navigation and threat contexts are rebuilt when boarding changes (`RunSession.HomeExtension.cs:261–274`). That division must be respected when extending a welded home across several vessels.

There are several different graphs:

| Graph/representation | Existing responsibility | Missing common contract |
|---|---|---|
| Generated rooms, floor cells and structural edge plan | Layout, boundaries, sockets, doors | Stable editable compartment identity and revision propagation |
| ShipNavGraph and Unity navigation/colliders | Walkability and threat movement | Rebuild/invalidation after every supported structural edit and connection type |
| SpatialPerception room portals | LOS and door/noise attenuation | Consistent acoustic consequences of new openings and assembled vessels |
| LifeSupport state and breach count | Ship-wide atmospheric health | Typed room volumes, internal openings, outside breaches and gas flow |
| Power grid/system health | Allocation and operational thresholds | Owned sources, storage, consumers and actual conduit connectivity |
| Dock/secured ownership graph | Vessel attachment, traversal, assembly load | General nested physical joins, service links and reversible connection lifecycle |

Mechanical, traversal, pressure, electrical and ownership connectivity should remain distinct graphs with explicit links. A weld should not implicitly grant air, electricity or ownership. A cut should not silently count as an external breach when it only joins two interior rooms.

## Working foundations and integration gaps

### Generation and exploration

`Procgen/ConstrainedExpedition.cs` publishes immutable `reclamation_expedition_v4`, preserves v3, uses bounded BSP generation, functional-role pairing, connected adjacency and at least two cycles. Budgets are 9–12 rooms for size 1 and 12–16 for size 2 before the dock addition. The current family is a rectangular single-deck envelope; all rooms are deck 0 (`:70–85`, `:133–134`). Validation can fall back to the purposeful generator with diagnostics (`:29–61`). `PurposefulExpedition.cs` retains service/cross-hull families and reserved furnishing cells. Therefore the generator is neither absent nor merely a fixed demo; however broad hull morphology, vertical circulation and distinct vessel purposes remain limited.

Normal travel enables richer new contacts after the first visited wreck, retaining each visited blueprint's profile (`RunSession.Travel.cs:154–157`). Rich profiles apply to size 1/2, not every category (`ShipGenerator.cs:184`). New Run and the first-away onboarding remain deliberately bounded. Old saved layouts are not upgraded in place. This protects save identity but means players can revisit old/simple layouts despite a new generator build.

Purposeful supplies are real: v4 medical rooms provide field medkits/bandages, crew supplies provide finite rations/water, maintenance supplies repair materials (`GameplaySliceBuilder.cs:159–196`). These are useful role-specific gameplay differences. The layout still largely starts with room geometry, then assigns roles, objectives, hazards and furnishings. A vessel's machinery, circulation, service network and incident history do not yet jointly explain why its wrecked state looks and plays as it does. Hard-coded room IDs and fixed supply budgets constrain diversity.

Custom and purchased assets both have importer/normalization paths. StructuralPrefabBuilder records identity, footprint, pivot/blocker contracts, bounds drift warnings and source bindings (`:177–188`, `:268–282`, `:435`). Custom 8 m contracts and damaged/opening variants should remain explicit adapters, not be stretched into arbitrary tiles. The purchased floor slice is a visual overlay; procedural geometry still owns collision/navigation. This is not full modular-pack adoption. No automatic archive scanning explains store-pack adoption only, not every custom-asset integration issue.

Further generation work should begin from **vessel purpose → coherent intact layout/infrastructure → incident history → persistent salvage state**. It should add meaningful route alternatives, reachable work anchors, distinguishable cargo/medical/engineering/living spaces and visually coherent assets. It should not merely increase room count or decorative renderer count.

### Structural work, reclamation and mobile homes

Module work, timed costs, salvage yields, repair and component installation are integrated (`RunSession.WorkAction.cs`). Secured home joins physically carve boundaries, install doors, rebuild composite navigation, preserve relative vessel placement and support walking both ways. Cached geometry identities avoid unconditional rebuilding every frame (`Runtime/Session/HomeAssemblyGeometry.cs:30–55`). Supported physical joins are currently bounded direct home attachments; the constructor filters secured members whose parent is the home (`:20–28`). A validated nested dock graph is not yet a general nested traversable structure.

Structural removal changes local renderers/colliders and marks navigation/atmospheric consequences. It does not establish a general piece/joint graph, split a vessel into independently owned pieces, or recompute its compartment network. `ModuleIntegrityConsequences` stores `atmosphere_link`; `RunSession.Fire.cs:465–474` adds module wall breaches and hull breaches into one count. Those are consequences, not a pressure-volume solver.

Assembly mobility is real. `Systems/Travel/AssemblyMobility.cs` traverses members once, rejects invalid/duplicate ownership, includes dry hull/cargo/installed mass and gates departure on owned operational propulsion and local power. Stowed craft do not contribute engines. This fulfills a useful bounded capability model; it is not Newtonian flight. Dry mass currently derives from floor area at 100 kg/m² and engine support at 125 kg/m² (`:40–45`), scaled by system/hull condition. Geometry cut away later does not automatically produce a revised physical mass model. Travel of a home assembly preserves relative transforms and location; its path does not charge a fuel/time budget (`RunSession.HomeExtension.cs:223`). Towing/recovery, removable secured joins and conduit sharing remain unclosed.

### Inventory, component identity and maintenance

Player and ship inventories contain stack counts, mass, capacity and equipment rules. UniqueItemState tracks unique claims; it is not a universal item-instance model. General volume, nested containers and persistent per-instance condition/provenance were not found in the traced inventory paths.

A concrete identity-loss risk exists across component salvage/remounting. Dismount returns item form/count/mass and linked IDs, but inventory receives an aggregated stack (`ComponentPlacementState.cs:390–426`; `ComponentMountResolver.cs:35–74`). Remounting the same slot can reuse its old entry, while another slot creates a fresh catalog-default condition (`ComponentPlacementState.cs:453–513`). Static tracing establishes that condition and original instance identity do not travel in the inventory item. It does not establish an observed player exploit. Persistent reclamation needs a transferable instance or explicit condition-bearing salvage contract.

Crafting, recipe knowledge, skill/tool/station gates and consumable repairs exist. Armor durability was repaired in earlier work; general tool/weapon wear, overhaul and replacement were not established across the traced paths. CookingState is explicitly retired in favor of kitchen crafting, so that obsolete class is not evidence that cooking is missing. The resource economy has not been closed across weeks of consumables, ammunition, welding, replacement parts, food, medicine, power and propulsion.

### Atmosphere, power, fire and survival

LifeSupportState tracks ship-wide O2/CO2/temperature/water and applies powered ratios plus breach-count effects (`Systems/Survival/LifeSupportState.cs:59–85`). This is a functioning abstraction, not room pressure, gas quantities or real leakage through an editable hull. Suits and oxygen reserves are integrated, and authored radiation/temperature sources take precedence over the fallback (`RunSession.Hazards.cs`; `RunSession.Tick.cs:240–365`). Do not repeat the obsolete claim that every away destination always applies blanket radiation.

Fire has adjacency, door gating, oxygen dependence, suppressant limits, power and damaged-system reignition. It is not a stub (`FireSuppressionState.cs:166–265`). Its active-session integration differs from absent-ship catch-up, and it is not evidence of a multi-vessel conserved atmosphere.

PowerGrid allocates health-scaled supply to priorities, but no complete fuel/battery/conduit conservation was demonstrated. The production coordinator passes `999` available power above a home allocation threshold (`RunSession.Crafting.cs:285`). Hydroponics and water recycling check power/inputs at start and then run timers; continuous outage consequences are not coherently represented. Models are home-centered rather than a durable station instance owned by whichever reclaimed vessel holds it.

Vitals, hunger, thirst, stamina, wounds, infection, temperature, radiation and sanity are real. Default hunger/thirst drains are 0.5/0.8 per second, before context multipliers; default passive healing is zero (`VitalsState.cs:16–25`). These imply short resource horizons, but choosing calendar/rate balance is a product decision. The failed fresh extended journey demonstrates that combat, untreated wounds and resupply ordering matter together. A diagnostic consumption/damage ledger and an earned repeat-expedition acceptance test are needed before calling the economy sustainable or impossible.

No integrated sleep/fatigue/calendar or safe accelerated time was found in the traced Core/Runtime paths. Standing still to regenerate stamina and pausing UI are not that system. PlayerController uses grounded capsule/gravity movement (`:211–224`); suit support is not proof of EVA propulsion, tethering, airlock procedures or vacuum recovery.

### Creatures, combat and biomass

The previously reported unlimited-range attacks are fixed: authoritative player targeting checks distance/aim/cooldown and enemies check attack range/LOS (`ThreatRuntime.cs:296`, `:321–348`, `:574–587`). Room signals and portal/door perception are wired (`RunSession.Tick.cs:413–490`; `SpatialPerceptionState.cs`). Movement noise, lighting and scent still use coarse signals; lighting is ship-power based, scent is hard-coded. Audio's occlusion heuristic is not the same room-acoustic model. These are integration limits, not universal through-wall attacks.

Threats are deterministic archetypes spawned from authored/generated markers. WebInfestationState grows/recedes scalar coverage and damages hull (`:18–54`). No living biomass stock, harvesting/reserve economy, territorial ownership, autonomous collectors, funded defenses or adaptive resource-driven creature generation was found in the traced game systems. Existing anatomy generation and gait do not implement that ecology. Implementing such a loop is a core requirement, but its bounded resource/rule model remains a design decision; machine learning is not implied.

The live Critter adapter validates production recipes, preserves seed/recipe identity, honors model references, disables imported collision, retains the game Mesh/hitbox/movement contract and falls back safely. Organic pools and mechanical drone treatment are separate (`Runtime/Session/Views/ThreatCreatureFactory.cs`). A missing saved recipe is not silently rerolled.

The companion's local `data/skeletons/crawler_alien_tripod_balanced_v3.skeleton.json:6` is now **approved**. A scan of v3 source skeletons found 80 approved and 11 draft. Older game integration docs claiming every crawler is draft are stale. This does not prove that the embedded pinned package, compiled library, approved artifact hashes and receipts match. No production CritterProductionLibrary was found in the searched game assets/Resources paths or serialized default host. Real approved-owner geometry in this game's normal encounters remains unverified; synthetic approved test fixtures are not that proof. Do not ask the owner to reapprove the unchanged approved crawler. Compare exact provenance, use the normal build/review gate and adopt only matching approved artifacts; retain missing-asset fallback and saved-recipe compatibility.

The embedded package declares Unity 6000.0 and Animation Rigging 1.3.0. Its root includes UPSTREAM provenance but no LICENSE file, and package.json has no license field; no top-level companion LICENSE was found. That observation does not establish redistribution rights or forbid the owner's local use. Before release, verify explicit permissions/attribution for companion code, generated/imported part sources, paid asset packs and redistributed dependencies. Proprietary geometry/textures remain local; this audit grants no new publication rights. V3 package schema compatibility is established only by the pinned integration tests, not every legacy companion authoring mode or future upgrade.

### Time, persistence, loss and information

Run/world snapshots, visited ship state, connection identity, mobile-home location and schema migration exist (`WorldSnapshotAssembler`; `SaveMigrationService`, run-6/world-4). Future world versions fail closed; old layouts retain their generator profile. Save/load is not an absent system.

Inactive ShipRuntime catch-up is capped at 1,800 seconds, advances systems/web/hull, then records the current world time (`ShipRuntime.cs:137–160`, `:206–220`). Longer elapsed intervals are discarded. It does not equivalently advance fire, threats, food or room atmosphere. Multi-week revisit persistence therefore needs an explicit simulation policy, not just more serialization. Offline wall-clock progression is undecided and should not be inferred from game-world catch-up.

FileSystemStorage writes a temporary file, deletes the destination, then moves the temporary file (`Core/Services/Storage.cs:194–204`). This avoids a partial destination write, but deletion/move is not an atomic replacement: interruption in that interval can leave only the temporary file. Existing corrupt-backup logic does not prove recovery from every interrupted write. Disk-backed interruption tests and replace/recovery policy remain needed.

Onboarding completion now checkpoints and continues (`RunSession.Objectives.cs:82–96`). Residual extraction concepts remain in SeaGraph's default goal, ChartPanel's “Extraction route” and EndRun's default reason (`RunSession.Save.cs`). Live assembly travel does not invoke SeaGraph's fuel/food charging helpers. Remove or reconcile those residual contracts with persistent navigation; do not revive an extraction victory.

Death freezes terminal run state; restarting and meta unlocks exist. A recoverable corpse/ship/world with a replacement survivor is not demonstrated. Whether death ends the entire world, permits successor survivors or allows other forms of recovery is a design choice. Map/sensors and chart UI exist, but meaningful unknown-contact discovery, observation confidence and navigation costs need a live integration pass. Avoid treating a chart's displayed fuel/food distance as an actual charged travel economy.

### Presentation, usability and operations

Orthographic zoom, UI-aware wheel input, full-wall assembly occlusion/restoration, ramps, focus selection and retained build shaders are real. Enemy visibility remains governed separately from wall hiding. The original shader-stripping failure was a genuine built-player defect; corrected code and player regression coverage exist. Preserve that build-retention strategy when adopting custom materials.

Player-readable room purposes, item silhouettes, work feedback and consistent warning state remain uneven. IconCatalog explicitly supplies category placeholders for missing art. Existing renders are not visual-quality acceptance. Accessibility has text reflow/scaling, captions/settings and other options; not every consumer or controller/device was verified. Localization/audio exist, but complete translation, captions and room-aware sound behavior were sampled, not certified. Native-input, multi-resolution and accessibility acceptance remain coverage gaps.

A prior RTX 4070 Laptop offscreen joined-home sample reported frame-interval median 14.26 ms/p95 18.20 ms over 355 samples. Service/cargo samples were about 17.15/23.25 and 17.92/25.12 ms. These are limited historical intervals, not a broad CPU/GPU profile, low-end guarantee or weeks-long performance soak. Renderer component counts include hidden objects and are not draw calls. Composite geometry caching is present; claims of unconditional per-frame navigation rebuild would be incorrect. World growth, retained visited ships, save size, memory and combat/biomass load remain unverified at scale.

## Prioritized gap register

Priority means impact on the stated experience, not a claim that every row is a reproduced defect. **D** = source-confirmed contract defect/contradiction; **I** = integration gap; **R** = static risk; **B** = balance question; **C** = missing design decision; **V** = validation gap.

| ID | Priority/type | Gap and player consequence | Dependency / closure evidence |
|---|---|---|---|
| G01 | P0 I/C | Inconsistent active/inactive simulation; neglected ships do not share a coherent elapsed-time history | Canonical world clock, subsystem ownership, bounded deterministic catch-up; active vs revisit equivalence tests |
| G02 | P0 I/C | Multi-expedition supply/recovery is not naturally proven; maintenance can collapse before reaching replenishment | Resource/damage ledger, finite earned supplies, treatment path; repeated journey with no grants |
| G03 | P0 I | Home production is threshold-powered/timer-driven rather than conserved owned machinery | G01, station instances, input/output and continuous power semantics; interruption/save/revisit tests |
| G04 | P0 R | Component identity/condition lost across inventory transfer/remounting | Instance/provenance contract; damaged A → salvage → B → save/restore exact condition |
| G05 | P0 I | Cuts/welds do not update a common editable compartment/service topology | Separate typed graphs with revision events; cut internal wall, open exterior breach, reseal, revisit |
| G06 | P0 I/C | Biomass enemy resource/territory/defense loop missing | G01/G05, bounded stock and funded behavior rules; player can observe/interdict harvesting and defense spending |
| G07 | P1 D | Extraction UI/default contracts contradict persistent survival | Remove legacy goal semantics without breaking legacy save migration; onboarding never ends life |
| G08 | P1 I | Travel display/cost helpers are disconnected from actual fuel/time/resource charging | Mobility-owned cost policy; exact ledger per craft/assembly departure and failed transaction |
| G09 | P1 I | General nested secured joins lack equivalent physical traversal | Validated mechanical graph + local assembly geometry; A–B–C, doors, traversal and save/restore |
| G10 | P1 I/C | Removal/towing/recovery and service connectors incomplete | Reversible ownership/connectivity transaction, safety checks, explicit air/power links |
| G11 | P1 I | Ship-wide air/breach counts cannot represent sealed reclaimed rooms | G05, room volumes and outside portals; isolating a leak preserves other compartments |
| G12 | P1 I | Power lacks conserved sources/storage/conduit topology | Owned generator/battery/fuel and consumer contracts; brownout, priorities, cut wire, saved outage |
| G13 | P1 I | Purpose/history generation does not causally structure infrastructure and damage | Semantic vessel blueprint, validation and incident pass; readable function and consistent salvage |
| G14 | P1 I | Current later family remains rectangular single-deck; long-horizon exploration variety is limited | Distinct hull/circulation families and traversal validation, preserved profile versions |
| G15 | P1 V | Repeated expeditions/relocation are not earned end-to-end acceptance | G02/G08, natural acquisition, treatment, home maintenance, move, return and restore |
| G16 | P1 I/V | Approved companion assets not demonstrated in normal production encounters | Exact artifact/package/library provenance and production gate; real approved rendered encounter and saved recipe |
| G17 | P1 R/V | Disk save replace/recovery interruption interval untested | FileSystemStorage/SaveLoad; interruption at write/delete/move, backup and migration tests |
| G18 | P1 I/C | EVA procedures/navigation/recovery missing despite suits | Pressure/airlock policy, limited movement/tether/tool inputs and real return path |
| G19 | P1 C | Death/world loss/successor recovery contract unresolved | Explicit product policy; preserved assets and readable recovery/final-loss behavior |
| G20 | P1 I/C | Rest/calendar/controlled time absent from long-haul loop | G01, safe-zone threat/time rules; no free healing or bypassed maintenance |
| G21 | P1 I/V | Resource-source/sink closure unproven for meds, ammunition, tools and replacements | Catalog audit and repeated finite economy test with depletion/recovery branches |
| G22 | P1 I | Detection cues coarse and sound perception/presentation diverge | Door/light/noise/scent ownership, explainable alert/attack feedback and stealth acceptance |
| G23 | P1 V | World growth, catch-up and large assembly performance unproven | Frame-time/memory/save-size profiles under combat and repeated revisits, low-end target decision |
| G24 | P2 I/C | Volume/nested container/provenance scope unresolved | Inventory schema and UI/transfer contracts; retain mass gates and useful small craft |
| G25 | P2 I | Wear/maintenance/replacement breadth uneven beyond armor | Condition-bearing instances; salvageable degradation and reversible repairs |
| G26 | P2 I | Static area-derived mass does not follow edited/salvaged structures | Versioned authored mass ledger; no double-counted installed/cargo/carried craft |
| G27 | P2 I | Map/sensor information and navigation risk not proven through repeated use | Unknown contacts, observation persistence, costs and truthful UI |
| G28 | P2 I/V | Art/labels/warnings can obscure actionable state | Representative room/HUD renders; focus/occlusion/default/zoom and warning consistency |
| G29 | P2 V | Accessibility, localization and native controls not comprehensively accepted | Device/resolution/text/caption/color/hold-input matrix, actual standalone sessions |
| G30 | P2 I/V | Test fixtures and historical documents can overstate natural progress | Explicit grant/fixture labels, consistent current results and journey resource logs |

## Player horizons and causal failure chains

**First hour:** title, default onboarding, physical movement, interaction, repair/training and first-away return/save have bounded evidence. The short-horizon survival clock makes clarity and reachable supplies important. Native-input acceptance of the final build and all supported starting configurations remain limited. Onboarding must explain shelter, oxygen, work costs and treatment before it demands prolonged repair.

**First shelter:** home commissioning and secured joins work in bounded paths. The installed fixture proves controls/traversal/transit but grants missing installation resources. Natural acquisition, treatment and continued operation are unclosed. Do not replace those assertions with free inventory or refill vitals to label this horizon complete.

**Repeated expeditions:** richer profiles and visited-state persistence exist; a natural cargo journey passed. Survival consumption, medicine, work stamina and return timing interact. The failed extended path reached lethal combined combat/wound/atmosphere exposure before treatment. A smarter controller or different player route may succeed; the current evidence neither proves sustainable balance nor universal impossibility.

**Weeks of maintenance/expansion:** no acceptance proof. Inactive catch-up loses elapsed time and subsystem equivalence; production/power are not conserved; transferable components lose identity; edited structure lacks complete service topology. These are causal architectural gaps, not decoration tasks.

**Relocation/recovery after loss:** owned aggregate capability is integrated, including small carried craft exclusions. Fuel/time, towing/detachment, general service sharing and death/recovery policy are unfinished. Proving a granted fixture can travel is narrower than earning relocation after several resource-consuming expeditions.

The key dependency chains are:

1. Stable ownership + world clock → persistent subsystem state → conserved production/maintenance → meaningful long-horizon resupply.
2. Semantic vessel blueprint → coherent rooms/services → incident damage → accessible salvage → reusable condition-bearing components.
3. Typed structural edits → updated traversal/pressure/power/perception → safe joins and containment → reliable mobile home.
4. Biomass stock/territory → harvesting and funded defenses → explainable adaptive threats → player counterplay and containment.

## Reference-game adaptation framework

References should provide useful design questions, not a feature checklist or automatic scope expansion.

| Reference lens | Core question for this game | Useful adaptation | Optional/excluded scope |
|---|---|---|---|
| Project Zomboid | Can a place become a legible, maintained shelter with resource/time pressure? | Persistent containers, readable threats, purpose-built cutaways, practical daily routines | Do not require identical world scale, simulation calendar or camera constants |
| FTL | Do connected rooms, doors, power and damage create understandable tactical decisions? | Clear subsystem priorities, containment and consequential room layout | Sector victory structure is incompatible with mandatory extraction; crew micromanagement is not required |
| Ostranauts | Does salvage retain ownership, condition and physical usefulness when moved into another vessel? | Component identity, repair/deconstruction and pressure/service continuity | Its exact control scheme, finance/social simulation and orbital model are not implied |
| Barotrauma | Can hull breaches, airlocks, power failures and creatures interact coherently? | Compartment containment, explainable machinery failures and maintenance | Multiplayer roles and coordinated crew requirements are optional |
| Space Haven | Can food/air/power/storage support a maintained mobile habitat? | Closed resource budgets and functional ship planning | Colony population management is not a required replacement for the current player loop |
| Cataclysm: Dark Days Ahead | Do item condition, containers, treatments and vehicles matter repeatedly? | Persistent item properties, recovery costs and useful small vehicles | Its enormous content breadth and turn-based abstraction are not required |
| Hardspace: Shipbreaker | Do a ship's structure, compartments and damage consequences agree? | Semantic modules and explicit structural/pressure graphs | Free-form cutting/rigidbody fragmentation is not automatically required |

The Hardspace technical presentation specifically describes piece/joint connectivity, linked pressure rooms/openings, and designer blueprints with nested hardpoints and curated variants. These support the architectural benchmark above; they do not mandate copying its technology. Primary source: [How to Dissect an Exploding Spaceship](https://media.gdcvault.com/gdcsummer2020/presentations/Harrison-Richard-HowToDissectAnExplodingSpaceship.pdf), pages 27–32 and 41–43. Other rows are adaptation questions, not factual claims of complete feature parity.

## Foundation-first roadmap and acceptance

**Stage 1 — Persistent state and honest evidence.** Define canonical clock/ownership and item-instance boundaries. Fix interrupted disk recovery and residual extraction semantics. Add a resource/damage ledger and a deterministic repeated-journey harness that retains assertions and labels every grant. Acceptance: active versus absent/revisited state reconciles under the chosen policy; damaged component transfers preserve identity; onboarding remains nonterminal; disk interruptions cannot silently discard the last good state.

**Stage 2 — Conserved shelter.** Make production and station state vessel-owned, gate ongoing operations on actual inputs/power and close the basic medicine/food/water/repair budget. Decide time/rest rates before rebalance. Acceptance: an earned shelter survives a declared multi-expedition horizon using finite supplies, outages interrupt appropriate work, and save/revisit preserves queues, outputs and consumption. This is not permission to disable survival or equalize classes.

**Stage 3 — Semantic generation and editable services.** Generate from vessel purpose and infrastructure, apply incident history, then publish typed compartment/service/traversal connections. Keep existing profile versions and asset contracts. Acceptance: representative seeds provide recognizable purposes, physically built alternate routes, reachable work anchors, coherent walls/openings, no illegal overlaps, and cuts/repairs update the right graphs. Large maps alone are insufficient.

**Stage 4 — General reclamation and relocation.** Extend supported joins, reversible detachment, towing/recovery and explicit conduits; reconcile authored mass with changed structures and charge a chosen mobility resource/time budget. Acceptance: naturally earn A–B–C shelter, seal/unseal/cut a supported join, retain small scout craft, relocate under owned capacity, revisit and restore exact geometry/ownership/services without duplication.

**Stage 5 — Bounded biomass ecology.** After the world/economy foundation, implement the agreed stock/reserve/harvest/defense rules, persistent territory and player counterplay. Adopt only provenance-matching approved creature artifacts. Acceptance: creatures obtain/spend measurable resources, defense strength has an explainable cause, containment/interdiction changes later expeditions, and saved recipes/ecology restore deterministically. Autonomous collectors, spread and adaptation breadth require a bounded design choice. Do not call scripted resource rules machine learning.

**Stage 6 — Soak and presentation acceptance.** Run multi-seed, class-path and sustained relocation/loss scenarios, actual native standalone sessions, representative visual captures and target-hardware profiles. Acceptance includes frame-time tails, memory/save growth, no misleading HUD warnings, readable purposes/interactions and supported accessibility. Tests should exercise meaningful consequences, not mirror methods or weaken failed assertions.

## Decisions needed before expanding scope

1. Game-time scale, rest/sleep, safe accelerated time and offline progression policy.
2. Fuel/energy/time cost of vessel and assembly travel, and what towing requires.
3. Death/world persistence, successor survivors and loss recovery.
4. Pressure fidelity and supported editable compartments; abstraction is acceptable if consistent.
5. Initial biomass resource/territory/collector/defense scope and adaptation limits.
6. Inventory volume/container/provenance breadth and tool/weapon wear expectations.
7. Target hardware/world-size/performance budgets; multiplayer, NPC crews, factions and social systems remain optional, not assumed core blockers.

Starter-hub expansion and class-balance changes remain separate pending decisions. The now-approved exact upstream crawler is not an unanswered owner-approval request; matching and validating the local production artifact still is technical work. No approval is granted to changed or draft assets by this audit.

## Current runnable path and next verification

Open `F:\tmp\synaptic-sea-playable\SynapticSea` with Unity 6000.6.0f1, or launch the distinct existing `mobile-home-20261001\TheSynapticSea.exe`. Preserve the user's running session and save files. New Run/first-away remain the guarded onboarding; later previously unvisited eligible contacts use the richer profile after a first wreck visit. Revisited contacts retain their saved profile. This is an access contract, not proof that every current save naturally unlocks every new family.

Before reporting the persistent game functional, repeat a natural resource-constrained journey through repair, treatment, reclamation, installation, relocation, scout return and disk-backed Continue. Resolve any actual product blocker while retaining assertions; label controller mistakes, balance questions and design choices separately. The present build is a working foundation with the limitations above, not a verified multi-week finished survival game.

## Appendix A — Explicit coverage register

Deep tracing means relevant callers, state authority, consequences and persistence were inspected for the described slice. It does not mean every line was reviewed. Runtime evidence here is retained evidence; nothing was newly executed during this audit.

| Area | Code coverage | Retained runtime evidence | Unverified or outside inspected scope |
|---|---|---|---|
| Title/bootstrap/lifecycle | Deep: default session, onboarding continuation, termination/save | Title/New Run and lifecycle tests | Native final-build input; all configurations |
| Movement/interaction/work | Deep: eligible dispatch, repair, home work; controller presentation sampled | Actual test-controller walking/reach/repair/ramps | All devices, tools and prolonged earned work budgets |
| Generation/exploration | Deep: v4 constraints, profile selection, furnishing/supply pass | Natural cargo and existing layout/render regressions | Every seed/size, multi-deck expeditions, long exploration pacing |
| Custom/purchased assets | Sampled: builders, normalization, bounds, variants and registration | Earlier floor/custom/ramp captures | Every proprietary model/material/license and full-pack module adoption |
| Docking/home/mobility | Deep: graph validation, direct-home physical joins, capability and persistence | Doors/two-way walking and installed/transit fixture | Nested general joins, towing and natural engine acquisition |
| Cuts/components | Deep: completion/consequences, dismount/inventory/remount | Bounded work regressions | General piece fragmentation and observed condition-reset exploit |
| Power/air/fire | Deep: coordination, allocation, breaches, catch-up differences | Models and journey hazards | Conserved room gas/wiring and equivalent absent outcomes |
| Food/craft/medicine | Deep: station gates, production timers/ownership, survival/wounds; catalog breadth sampled | Supplies/consumption/death records | Every recipe/source, sustainable weeks or every class path |
| Combat/perception | Deep: authoritative range/LOS, room/door signals, restore | Combat regressions and encounter journeys | Every archetype balance, biomass ecology, audio parity |
| Critter integration | Deep: adapter/identity/fallback; approval status sampled | Approved synthetic fixtures | Real matching owner-approved library in normal encounters; all authoring modes |
| Save/time/migration | Deep: snapshots, visited state, location, disk adapter and catch-up | MemoryStorage Continue/migration regressions | Interrupted disk replacement, multi-week/offline outcomes |
| Death/recovery | Deep: terminal slots/results; scoped successor search | Lifecycle death/restart | Approved world-loss/successor policy and recovery journey |
| UI/camera/audio/a11y | Sampled: current consumers/settings/visibility/shaders | GPU camera/ramp/shader captures/tests | Final scene quality, all translations/captions/devices/native access |
| Performance/scale | Sampled: caches/state lifetime and prior profiles | Bounded offscreen GPU intervals | Low-end, large combat assemblies, world memory/save growth |
| NPC/factions/multiplayer | Scoped discovery only | None claimed | Product intent; not assumed required |

## Appendix B — Full source traceability

These are complete repository-relative file paths, rooted at `F:\tmp\synaptic-sea-playable`; explicit companion paths use `D:\critter-creator`. Anchors refer to the audited checkout. Absence findings are limited by Appendix A; they are not claims that every file was searched or every action reproduced.

| Gaps | Files, symbols and anchors | Evidence boundary |
|---|---|---|
| G01 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs`, `TickPresentShips`, :128–145; `SynapticSea/Assets/_Project/Core/Session/RunSession.Ships.cs`, `RuntimeFor`/`CatchUpShip`, :42–64; `SynapticSea/Assets/_Project/Core/Systems/Travel/ShipRuntime.cs`, `Advance`/`CatchUp`, :137–160, :206–220 | Present-root tick versus capped inactive simulation and timestamp; no weeks-long run claimed |
| G02/G15/G21 | `SynapticSea/Assets/_Project/Core/Systems/Survival/VitalsState.cs`, defaults :16–25; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs`, stamina :765; `SynapticSea/Assets/_Project/Core/Procgen/GameplaySliceBuilder.cs`, supplies :159–196; `builds/logs/mobile-home-final-supported-play.xml`; `builds/logs/assembly-earned-early-seal-fresh.xml` | Costs/supplies and exact failed journeys; no universal impossibility claim |
| G03 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs`, `BuildProductionStations`, :267–305, power :285, `AdvanceProduction`, :719; `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs`, :385–392; `SynapticSea/Assets/_Project/Core/Systems/Food/HydroponicsState.cs`, :46–70; `SynapticSea/Assets/_Project/Core/Systems/Food/WaterRecyclerState.cs`, :44–65; `SynapticSea/Assets/_Project/Core/Session/Interactables/ProductionStation.cs`, :119–263 | Real inputs/output and timers, home ownership and threshold power |
| G04/G25 | `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ComponentPlacementState.cs`, `Dismount`, :389–426, `Mount`, :434–513; `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ComponentMountResolver.cs`, :35–74; `SynapticSea/Assets/_Project/Core/Session/RunSession.Combat.cs`, armor profile path | Condition-bearing placement versus stacked transfer/default remount; separate persistent armor behavior |
| G05/G11 | `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ModuleIntegrityConsequences.cs`, :110–154, :436; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs`, :960–989; `SynapticSea/Assets/_Project/Core/Session/RunSession.Fire.cs`, `DerivedBreachCount`, :465–474; `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs`, :457–500; `SynapticSea/Assets/_Project/Core/Systems/Survival/LifeSupportState.cs`, :59–85 | Flags/colliders/nav consequences and combined breach count; not a typed room-flow model |
| G06 | `SynapticSea/Assets/_Project/Core/Systems/Survival/WebInfestationState.cs`, :18–54; `SynapticSea/Assets/_Project/Core/Session/Runtime/ThreatRuntime.cs`, :131–139, :473–526; `docs/colony-defense-proposal.md` | Scalar infestation and marker spawning; proposal not implemented economy |
| G07 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Objectives.cs`, `OnInteractableCompleted`, :82–96; `SynapticSea/Assets/_Project/Core/Session/RunSession.Save.cs`, `EndRun`, :22–60; `SynapticSea/Assets/_Project/Core/Systems/Travel/SeaGraph.cs`, :18, :53–69; `SynapticSea/Assets/_Project/UI/Panels/ChartPanel.cs`, :35 | Nonterminal onboarding plus residual extraction contracts |
| G08/G27 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Travel.cs`; `SynapticSea/Assets/_Project/Core/Session/RunSession.HomeExtension.cs`, `TravelHomeAssembly`, :223; `SynapticSea/Assets/_Project/Core/Systems/Travel/SeaGraph.cs`, `ApplyTravelCost`, :396; `SynapticSea/Assets/_Project/UI/Panels/ChartPanel.cs`, :68–85 | Live movement versus display/helper charging; scoped caller search found no live charge |
| G09/G10 | `SynapticSea/Assets/_Project/Runtime/Session/HomeAssemblyGeometry.cs`, `Reconcile`, :20–55, `HideBoundary`/`Restore`, :58–76; `SynapticSea/Assets/_Project/Core/Session/RunSession.HomeExtension.cs`, `RebuildHomeJoinControls`, :84, :176–215; `SynapticSea/Assets/_Project/Core/Systems/Travel/DockingManager.cs` | Bounded direct-home geometry and validated graph; not general service/reversible assembly support |
| G12 | `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/PowerGridState.cs`, :73–75; `SynapticSea/Assets/_Project/Core/Session/RunSession.Ships.cs`, `RecomputeExpandedShipSystems`, :85–130; `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs`, :285 | Health-scaled power allocation/home threshold, not an energy ledger |
| G13/G14 | `SynapticSea/Assets/_Project/Core/Procgen/ConstrainedExpedition.cs`, :12–31, :36–85, :133–134; `SynapticSea/Assets/_Project/Core/Procgen/PurposefulExpedition.cs`, :10–16, :46–48, :87–127; `SynapticSea/Assets/_Project/Core/Procgen/ShipGenerator.cs`, :184, :403–417; `SynapticSea/Assets/_Project/Core/Session/RunSession.Travel.cs`, :154–157 | Real family/profile/constraints and live selection, limited hull/deck variety |
| G16 | `SynapticSea/Assets/_Project/Runtime/Session/Views/ThreatCreatureFactory.cs`, :18–20, :29–53, :65–99; `SynapticSea/Assets/_Project/Runtime/Session/RunSessionHost.cs`, :37, :111; `SynapticSea/Packages/com.ninthlevelsoftware.crittercrafter/UPSTREAM.md`; `D:\critter-creator\data\skeletons\crawler_alien_tripod_balanced_v3.skeleton.json`, :6 | Real adapter/pin, approved local source; actual compiled production match remains unverified |
| G17 | `SynapticSea/Assets/_Project/Core/Services/Storage.cs`, `FileSystemStorage.WriteText`, :194–204; `SynapticSea/Assets/_Project/Core/Systems/Save/SaveLoadService.cs`, `SaveWorld`/`LoadWorld`, :114, :148, :682–725; `SynapticSea/Assets/_Project/Core/Systems/Save/SaveMigrationService.cs`, :15–28, :70–103; `SynapticSea/Assets/_Project/Core/Session/WorldSnapshotAssembler.cs`, :66–79, :149–168, :361–454 | Real storage/migrations/preflight and untested replace interruption interval |
| G18 | `SynapticSea/Assets/_Project/Runtime/Player/PlayerController.cs`, :46, :211–224; `SynapticSea/Assets/_Project/Core/Session/RunSession.Hazards.cs`, :136–229 | Grounded movement and suits, no claimed EVA journey |
| G19 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Save.cs`, `EndRun`, :22–60; `SynapticSea/Assets/_Project/Core/Systems/Save/SaveLoadService.cs`, freeze :494; `SynapticSea/Assets/_Project/UI/Menus/RunResultsPanel.cs`, :151–181 | Terminal slots/results, no approved successor-world policy |
| G20 | `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs`, clock :29–49; `SynapticSea/Assets/_Project/Core/Systems/Survival/VitalsState.cs`, :16–25; `SynapticSea/Assets/_Project/Core/Session/TickStages.cs`, :139–153 | Realtime tick/recovery; rest/calendar not found in traced Core/Runtime paths |
| G22 | `SynapticSea/Assets/_Project/Core/Session/Runtime/ThreatRuntime.cs`, :296–348, :574–587; `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs`, :413–490; `SynapticSea/Assets/_Project/Core/Systems/Combat/SpatialPerceptionState.cs`, :16–22; `SynapticSea/Assets/_Project/Runtime/Audio/AudioManager.cs`, :591 | Fixed combat authority/portal perception and coarse audio/signal inputs |
| G23 | `SynapticSea/Assets/_Project/Runtime/Session/HomeAssemblyGeometry.cs`, `Reconcile`, :30–55; `SynapticSea/Assets/_Project/Core/Session/RunSession.Ships.cs`, `RuntimeFor`, :42–55; `docs/playability-audit.md`, historical profiles | Some caches exist; retained bounded intervals not large-world proof |
| G24 | `SynapticSea/Assets/_Project/Core/Systems/Inventory/InventoryState.cs`, :19, :77; `SynapticSea/Assets/_Project/Core/Systems/Inventory/ShipInventory.cs`, :9, :49–65; `SynapticSea/Assets/_Project/Core/Systems/Inventory/UniqueItemState.cs`, :11–43 | Counts/mass/capacity/unique claims rather than universal item instances |
| G26 | `SynapticSea/Assets/_Project/Core/Systems/Travel/AssemblyMobility.cs`, `CreateSpecification`, :40–45, `Evaluate`, :53–105; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs` | Static authored area mass plus owned payload/engine evaluation, not edit-derived mass |
| G28 | `SynapticSea/Assets/_Project/Runtime/Content/IconCatalog.cs`, :10–11, :83; `SynapticSea/Assets/_Project/Runtime/Player/IsoCameraRig.cs`, :26–29, :99–119; `SynapticSea/Assets/_Project/Runtime/Player/InteriorOcclusion.cs`, :35–87, :121–166, :216; `SynapticSea/Assets/_Project/Runtime/Session/Interaction/InteractableView.cs`, :306–307; `SynapticSea/Assets/_Project/Core/Session/RunSession.Interact.cs`, :33–56 | Real camera/occlusion/eligible targeting and placeholder art; warning cause unconfirmed |
| G29 | `SynapticSea/Assets/_Project/UI/Presenters/AccessibilitySettings.cs`, :40–72; `SynapticSea/Assets/_Project/UI/Hud/HudRoot.cs`, `ApplyAccessibility`, :275; `SynapticSea/Assets/_Project/Core/Systems/Infra/SettingsState.cs`, :59–72, :184 onward; `SynapticSea/Assets/_Project/Runtime/Audio/AudioManager.cs`, `DrainCaptions`/`PumpCaptions`, :310–314 | Settings and consumers exist, full native/accessibility certification not performed |
| G30 | `docs/playability-audit.md`; `docs/design/persistent-survival.md`; `docs/design/mobile-home-transit.md`; `docs/critter-runtime-integration.md`; `docs/inventory/SYSTEM_INVENTORY.md`; listed XML/TRX logs | Separate current evidence from stale status/percentages and historical results |

Reference synthesis should link the separate primary-source research to these gaps/dependencies, retaining optional/excluded boundaries. The adaptation framework is not a substitute for that research and does not imply feature parity. This document remains uncommitted/unuploaded; combined report delivery is separate from code publication.
