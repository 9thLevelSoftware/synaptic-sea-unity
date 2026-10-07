# Predeparture repair source correction

Revision `840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48`. This supersedes the earlier no-repair0-producer/trap candidate wording. Freshly compiled current Core, actual catalogs and finite authored cache; no Unity, natural input walk, user profile or grant of fire/hub perk. Cargo seal uses actual BreachSealPoint and repairs actual RepairPoint completion handlers. All11 class objects configured; three locked titles remain locked on fresh meta.

Normal source traced: initial cargo hull health0.3/breach_open=true -> BuildBreachSealPoints -> normal interaction registry -> strict-range4s seal consuming1 of cache6 hull_sealant -> repairXP15 directly, without cross penalty. Cargo source is finite. Fire suppression gives direct repairXP10 but default HomeSpawnSafety=1 permanently skips home fire seeds/reignition; there is no safety expiry clock. Authored channel xp_event repair45 is not additionally emitted by either wrapper.

| Class | After cargo seal level/XP/fraction | Final finite-cache model level/XP/fraction | Repair2 | Remaining effective XP | Natural walk witnessed |
|---|---|---|---|---:|---|
| engineer  | 5/495/0.000 | 5/495/0.000 | model feasible | 0.000 | no |
| mechanic  | 6/295/0.000 | 6/295/0.000 | model feasible | 0.000 | no |
| medic  | 1/164/0.500 | 1/164/0.500 | model insufficient | 35.500 | no |
| pilot  | 2/35/0.000 | 2/135/0.000 | model feasible | 0.000 | no |
| scientist  | 3/342/0.000 | 3/342/0.000 | model feasible | 0.000 | no |
| cook  | 1/8/0.000 | 1/48/0.000 | model insufficient | 152.000 | no |
| security  | 2/11/0.500 | 2/101/0.500 | model feasible | 0.000 | no |
| communications  | 1/21/0.500 | 1/66/0.500 | model insufficient | 133.500 | no |
| salvage_captain (title locked) | 2/58/0.500 | 2/168/0.500 | model feasible | 0.000 | no |
| field_medic (title locked) | 1/8/0.000 | 1/48/0.000 | model insufficient | 152.000 | no |
| signal_specialist (title locked) | 1/35/0.000 | 1/85/0.000 | model insufficient | 115.000 | no |

Sequence: greedily source-feasible repairs from the authored cache before objective XP; emit exact two120XP objective events and mirror their mapped force-repairs; perform cargo seal; finish newly source-feasible repairs. No random cache roll, repeated hazard damage, remount, skill books, hub perks or fire granted. Engineer/mechanic/scientist begin above gate; pilot/salvage captain require preserving low-level repair opportunities before scripted objective repairs; security reaches2 through cargo seal. Cook/field_medic bootstrap from0/96 to1/8 through cargo seal, then star_charts to1/48. Medic/communications/signal specialist remain below2 in this finite-cache model. These unsatisfied edges are NOT proof of global normal-run deadlock: runtime/system damage, other finite random rolls and existing meta need separate source-and-input experiments.

Owned hub reactor booster multiplies technical XP1.1; owned drydock adds1 repair at new-run configuration. Booster requires225 meta currency total; drydock prerequisite closure totals1200 (workshop75+medical75+scanner100+armory100+reactor150+command300+drydock400). Live menu purchase is connected but these are existing persistent meta, never assumed on a new profile. Leadership XP formula is unused; class technical multiplier and bus cross-training are live. Decode grants signal_analysis, without repair spillover. Repair has no skill-tree/book prerequisite.

All live producers and excluded misleading definition/helper paths are enumerated in predeparture-repair-producers.json. In particular mount_component repair45 is dynamically emitted by session completion but lacks a normal wrench source; commission_home_propulsion actually emits weld_panel (welding), despite authored repair xp_event.

Highest-value discriminator: security natural predeparture cache acquisition -> battery_cells and star_charts repair BEFORE restore_systems objective -> two onboarding completions -> cargo seal -> nav_linkage repair2. Compare cook cargo seal bootstrap and default zero burning home compartments. No report may convert this model witness into a natural walk witness.

Exact evidence (file:line:symbol):
- `SynapticSea/Assets/_Project/Core/Session/Interactables/BreachSealPoint.cs:76:TryStart` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Session/Interactables/BreachSealPoint.cs:180:Complete` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:69:BuildBreachSealPoints` (same published file: False).
- `SynapticSea/Assets/StreamingAssets/data/ship_systems/hull_compartments.json:6:cargo` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Session/Interactables/FireSuppressionPoint.cs:186:Complete` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Fire.cs:636:BuildFireSuppressionPoints` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Fire.cs:378:SeedFiresFromDamage` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Fire.cs:358:BuildFireContext` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Hazards.cs:198:HomeSpawnSafetyActive` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSessionDeps.cs:153:HomeSpawnSafety` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/Interactables/RepairPoint.cs:224:Complete` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:146:OnRepairCompleted` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Objectives.cs:57:OnObjectiveCompleted` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:809:TickWorkAction` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:850:TickWorkAction` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:217:TryWorkActionInteract` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.HomeExtension.cs:198:CompleteHomeWork` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Build.cs:210:ConfigurePlayerProgression` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Build.cs:223:ConfigurePlayerProgression` (same published file: False).
- `SynapticSea/Assets/_Project/UI/Menus/MenuCoordinator.cs:902:hub_upgrade_confirm` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:162:OnVoiceLogPlayed` (same published file: False).
- `SynapticSea/Assets/_Project/Core/Systems/Progression/PlayerProgressionState.cs:165:GrantXp` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Systems/Progression/SkillEffectsResolver.cs:271:XpMultiplier` (same published file: True).
- `SynapticSea/Assets/_Project/Core/Systems/WorkActions/WorkActionDriver.cs:285:ApplyXp` (same published file: True).
