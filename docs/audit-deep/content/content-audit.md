# Deep content audit

Revision `840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48`. Original static read-only pass; Unity was not executed. Followup freshly compiles engine-free current Core models: see class-deconstruction-baseline-followup.md and fresh-model-experiment-manifest.json. No player profiles or user saves are touched. Original findings remain source deductions except where the followup explicitly supplies model evidence.

## Exact scope

115 merged item definitions,33 material definitions,62 recipes (55 craft/7 deconstruction),9 loot tables/51 entries,11 component types/9 role sets,6 systems/18 repairs,22 skills/12 books/11 prerequisite rows,11 classes (8 base/3 unlockable),6 spatial craft stations (salvage included),2 production stations and2 crops. Full definitions and typed dependency edges are in dependency-ledger.json and dependency-graph.json. Data and source SHA256 manifests bind evidence to this checkout.

Normal New Run supports seed17 / breach_field / standard; other setup choices fail closed. Class definitions differ from unlocked title availability. Starting bag is empty. The live authored home cache supplies10 stacks/20 units, once, and must be reached. Current reclamation generator adds finite medical, crew and maintenance contents; fixtures/grant helpers are not counted as normal sources.

## Findings

### CONTENT-01: Component salvage requires an unavailable wrench

Classification: normal acquisition blocker.
11 component types are populated from role sets but ordinary dismount/remount requires wrench or tool_wrench; neither is registered or supplied by any item, loot, recipe, authored home content, live generator grant or pickup. Component item forms consequently have no demonstrated normal inventory acquisition.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:214:TryWorkActionInteract`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/ItemDefs.cs:44:LoadDefinitions`; `SynapticSea/Assets/StreamingAssets/data/components/component_catalog.json:3:components`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-02: Carried component forms lack inventory definitions

Classification: latent disconnected schema defect.
All 11 component item_form IDs are absent from merged ItemDefs. Unknown inventory weights default to zero, max stack to99 and category empty. Component catalog mass is never used by InventoryState; deposit-all excludes empty category. Normal player acquisition is currently wrench-blocked, so this is not presented as a normal-play zero-weight exploit.

Evidence: `SynapticSea/Assets/_Project/Core/Systems/Inventory/ItemDefs.cs:147:WeightEach`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/ItemDefs.cs:150:MaxStack`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/CargoTransfer.cs:49:HAULABLE_CATEGORIES`; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:791:TickWorkAction`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-03: Most recipe inputs have no supported source

Classification: normal content availability gap.
Even a permissive upper bound that grants access to all9 loot tables, all component role candidates and both crop products leaves42/62 authored recipes blocked by17 ingredient IDs.31/115 registered items have no enumerated acquisition edge at all. This denominator is not a runtime completion rate.7 deconstruction recipes remain among the20 source-feasible recipes; at most13/55 non-deconstruction recipes survive this source-only upper bound.

Evidence: `SynapticSea/Assets/StreamingAssets/data/recipes/recipe_definitions.json:12:recipes`; `SynapticSea/Assets/StreamingAssets/data/materials/material_definitions.json:3:materials`; `SynapticSea/Assets/StreamingAssets/data/items/loot_tables.json:3:generic_crate`
Upper-bound static closure ignores quantity, room geometry, tool, station, tier, class progression and finite depletion; cannot overstate availability as reachability.

### CONTENT-04: Tier upgrades are discarded by spatial craft precheck

Classification: normal UI gate defect.
The only2 recipes with nonzero station tier (craft_sensor_module tier1 and craft_thruster_nozzle tier2) are listed and prechecked with omitted stationTier parameter, default0. TryCraftRecipe rejects before BeginCraft can use station.EffectiveTier. Both recipes also have unavailable ingredients, so this is an independent latent gate behind the source blocker.

Evidence: `SynapticSea/Assets/_Project/Core/Session/Interactables/CraftingStation.cs:143:TryCraftRecipe`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:580:ListStationRecipeEntries`; `SynapticSea/Assets/_Project/Core/Systems/Crafting/CraftingState.cs:332:BeginCraft`; `SynapticSea/Assets/StreamingAssets/data/recipes/recipe_definitions.json:89:craft_sensor_module`; `SynapticSea/Assets/StreamingAssets/data/recipes/recipe_definitions.json:104:craft_thruster_nozzle`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-05: Recipe knowledge and skill books are disconnected

Classification: disconnected/legacy systems.
RecipeKnowledgeState exists only as a model and CraftingState optional argument; session creates no instance, passes no knowledge, emits no LearnFromBook/CodeX/RegisterDismantle and saves no knowledge summary.61 recipes author starter and1 book; the sole book recipe has no knowledge_book_id.12 skill books are not registered inventory items and no non-test caller invokes GrantXpFromBook.2 advanced skill nodes require those books or matching codex IDs that normal catalog does not grant.

Evidence: `SynapticSea/Assets/_Project/Core/Systems/Crafting/RecipeKnowledgeState.cs:67:LearnFromBook`; `SynapticSea/Assets/_Project/Core/Systems/Crafting/CraftingState.cs:104:CanCraft`; `SynapticSea/Assets/_Project/Core/Systems/Progression/PlayerProgressionState.cs:211:GrantXpFromBook`; `SynapticSea/Assets/StreamingAssets/data/player/skill_tree.json:4:welding_mastery`; `SynapticSea/Assets/StreamingAssets/data/player/skill_tree.json:5:biomatter_diagnostics`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-06: Claimed vessels do not inherit home services

Classification: special identity/coverage gap.
BuildCraftingStations and BuildProductionStations clear current stations then return whenever AwayFromStart or home unavailable. Boarding a secured claimed home-member sets AwayFromStart if target != HomeShip and rebuilds both. Thus ownership, repaired power and shelter on reclaimed non-home vessels do not instantiate station crafting or production; portable craft/wound treatment still work. No home relocation/promotion workflow was found.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:200:BuildCraftingStations`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:267:BuildProductionStations`; `SynapticSea/Assets/_Project/Core/Session/RunSession.HomeExtension.cs:266:ActivateBoardedContext`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-07: Medbay surgery depends on source-less gauze

Classification: normal acquisition blocker.
Medbay surgery requires medical_gauze, but gauze has no loot/recipe/production/pickup/authored source. The medbay skill bypass itself is a design choice. Normal wound bandaging instead accepts bandage_kit and normal direct healing accepts field_medkit; those2 types have current generated medical-room supplies.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:176:TryMedbaySurgery`; `SynapticSea/Assets/_Project/Core/Session/RunSession.cs:107:BANDAGE_ITEM_IDS`; `SynapticSea/Assets/_Project/Core/Procgen/GameplaySliceBuilder.cs:186:Build`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-08: Full item stacks consume finite loot containers

Classification: intentional loss policy with irreversible depletion.
LootContainer marks searched after attempting grants even if AddItem accepts zero or only a partial stack. It does not retain contents for retry. Looted IDs are persisted, so finite maintenance/medical/crew supplies lost to stack overflow stay lost on revisit/load. This is explicitly commented policy, not a weight-cap bug: InventoryState weight is soft capped.

Evidence: `SynapticSea/Assets/_Project/Core/Session/Interactables/LootContainer.cs:114:TryInteract`; `SynapticSea/Assets/_Project/Core/Session/Interactables/LootContainer.cs:142:GrantAuthoredContents`; `SynapticSea/Assets/_Project/Core/Systems/Travel/ShipInstance.cs:158:GetSummary`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/InventoryState.cs:102:AddItem`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-09: Queued craft completion bypasses ingredient consumption

Classification: disconnected legacy path.
FinishCraft starts the next queued recipe and changes active recipe without ConsumeIngredients, skill/tier/knowledge or output-space checks. No live UI/Session call to EnqueueCraft exists, so do not classify as reachable free-craft exploit.

Evidence: `SynapticSea/Assets/_Project/Core/Systems/Crafting/CraftingState.cs:437:FinishCraft`; `SynapticSea/Assets/_Project/Core/Systems/Crafting/CraftingState.cs:365:EnqueueCraft`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-10: Generic structural work lacks continuous site validation

Classification: normal candidate requiring runtime.
TickWorkAction continuously checks site/range/LOS only for secure_connection, commission_home_propulsion and cut_web_attachment. Generic cutting/prying/component work can continue with held input after moving away and completes against current ModuleIntegrityMap/ComponentPlacementState. The component variants are additionally blocked by missing wrench. Parent should verify normal structural cut case before promoting severity.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:738:TickWorkAction`; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:196:TryWorkActionInteract`; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:109:ApplyWorkYieldsToInventoryState`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-11: Starting class levels and uniform fabrication checks

Classification: design choice/functional specialization gap.
All spatial station recipes use fabrication rather than station-specific cooking/pharmacology. Engineer/mechanic start fabrication1, all other9 classes0. Portable recipes skip skill gating and use fabrication only for quality. Initial shuttle nav_linkage repair2 is immediately available to engineer/mechanic/scientist (3/11;3/8 base), home propulsion repair4 to mechanic (1/11). This is not evidence of class deadlock: objectives award repairXP, repair points train, hub bonus can add repair1.

Evidence: `SynapticSea/Assets/_Project/Core/Session/Interactables/CraftingStation.cs:90:PlayerSkill`; `SynapticSea/Assets/StreamingAssets/data/player/classes.json:3:classes`; `SynapticSea/Assets/StreamingAssets/data/ship_systems/systems.json:46:nav_linkage`; `SynapticSea/Assets/StreamingAssets/data/work_actions/work_action_catalog.json:19:commission_home_propulsion`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Objectives.cs:57:OnInteractableCompleted`
Static source proof; live runtime verification belongs to parent lane.

### CONTENT-12: Reload disables hold requirement for active work

Classification: normal candidate requiring runtime.
RunSnapshotAssembler saves active/interrupted WorkActionState and RunSession.Save restores it but sets _workRequiresHold=false regardless HoldToWork settings. A current held structural action can therefore resume progress after reload without held interact; parent must verify normal start/save/reload sequence.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSnapshotAssembler.cs:121:WorkActionSummary`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Save.cs:531:Apply`
Static source proof; live runtime verification belongs to parent lane.

## Negative findings

NEG-01: Portable crafting is connected: field_craft input opens picker, explicit start consumes ingredients, synthetic powered station advances, completion deposits, summaries restore. No missing spatial station defect.

Evidence: `SynapticSea/Assets/_Project/Runtime/Player/PlayerController.cs:116:OnFieldCraft`; `SynapticSea/Assets/_Project/Runtime/Session/RunSessionHost.cs:532:OnPlayerFieldCraft`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:464:RequestFieldCraft`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs:83:StageFieldCraft`; `SynapticSea/Assets/_Project/Core/Session/RunSnapshotAssembler.cs:52:Build`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Save.cs:435:Apply`

NEG-02: Repair reserves no parts at start, cancels leaving strict range/player invalid, rechecks part/tool/skill at completion before consumption. Only player carried inventory is queried; no automatic closed-room/other-vessel cargo borrowing.

Evidence: `SynapticSea/Assets/_Project/Core/Session/Interactables/RepairPoint.cs:103:PrecheckReason`; `SynapticSea/Assets/_Project/Core/Session/Interactables/RepairPoint.cs:11:Process`; `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ShipSystemsManager.cs:207:RepairWithInventory`

NEG-03: Ordinary station interaction checks LOS plus strict range; recipe picker freezes player. Missing confirmation-range check alone is insufficient proof of remote station use.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.Interact.cs:18:CanFocusInteractable`; `SynapticSea/Assets/_Project/Core/Session/Interactables/CraftingStation.cs:97:TryInteract`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:489:OpenSharedRecipePicker`

NEG-04: Junk salvage prechecks every output stack before consuming junk, preventing ordinary consume-with-no-space loss; inventory weight is soft-cap by design. Cargo transfers debit only destination accepted quantity.

Evidence: `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/DeconstructionResolver.cs:261:SalvageJunkItem`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/CargoTransfer.cs:115:MoveItem`; `SynapticSea/Assets/_Project/Core/Systems/Inventory/InventoryState.cs:102:AddItem`

NEG-05: Existing-slot component remount preserves saved condition; only fresh slot uses catalog default. Remount explicitly raises linked system health to minimum0.55, and ship-mod load reapplication also does so. Carried item form has no provenance/condition data; identity laundering is a parent runtime candidate, not proved normal exploit because wrench absent.

Evidence: `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ComponentPlacementState.cs:479:Mount`; `SynapticSea/Assets/_Project/Core/Systems/ShipSystems/ShipSystemsManager.cs:280:RestoreSubcomponentOnRemount`; `SynapticSea/Assets/_Project/Core/Session/RunSession.WorkAction.cs:1103:ReapplyShipModRuntimeEffects`

NEG-06: New loot/crafted food does not reset already tracked spoilage: RegisterFoodForSpoilage returns when item ID tracked; summaries restore food age. Model is per item type, not per stack/instance; storage-specific ageing and stack freshness blending need separate design/runtime assessment.

Evidence: `SynapticSea/Assets/_Project/Core/Session/RunSession.Crafting.cs:329:RegisterFoodForSpoilage`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Tick.cs:104:TickFoodRuntime`; `SynapticSea/Assets/_Project/Core/Session/RunSession.Save.cs:479:Apply`

## End-to-end traces

### physical salvage => carry => remount/ship-mod install => repair

definition: component catalog11 types / work_action_catalog actions / systems18 repair defs

ui_input: Interact Request => lowest-priority TryWorkActionInteract (wrench gate unavailable); ship-mod panel separately selects inventory form

mutation: ResolveDismount toggles mounted false, adds form to copied work bag then live inventory; ResolveMount debits form and restores linked sub to>=0.55; repair point rechecks categories/skill then removes each required part1

tick: TickWorkAction holds/stamina/wound speed; RepairPoint.Process strict-range cancel; only home join actions continuous LOS recheck

save_load_absence: placement per ship persisted plus current run summary; ship-mod manifest global saved and Reapply runtime effects; active/interrupted generic WorkActionState is saved by RunSnapshotAssembler:121-131 and restored by RunSession.Save:524-531 with _workRequiresHold=false; pending yields and floor WorkYieldDrops have no persisted world identity

failure: no wrench source blocks normal physical chain; all11 forms unknown weight/category; full-stack dismount AddItem return ignored; repair lost parts completion blocks safely; claimed nonhome lacks home service stations

### explore => loot => treat => return

definition: 9 loot tables51 entries; generated medical fieldmedkit2 bandagekit2, crew ration8 water8, maintenance reactor1 power2 filter2 sealant1

ui_input: scanner/travel board current generated vessel; LOS/range interact => LootContainer; hotbar consume fieldmedkit or Wounds bandage/treat panel

mutation: finite contents override random table; mark searched once; inventory only accepted quantity; bandage consumes bandagekit, treat accepts medkit/stim_pack/antibiotic; fieldmedkit directly heals but is not Wounds Treat input

tick: wounds infection/heal and vitals attrition run on both locations; spoilage per-item-type and home production run while away

save_load_absence: ShipInstance searched IDs, pending corpse records, bag quantities, WoundSummary, SpoilageSummary persisted; missing legacy optional wound summary fresh state; return rebuilds original HomeShip stations

failure: searched full-stack contents irretrievable; source-less medical_gauze surgery and craft_medkit blocked; generated fieldmedkit healing/bandage works; claimed nonhome cannot replace HomeShip services

## Limits and open runtime questions

Source-only closure is an upper bound, not a completion or normal-play reachability percentage. Geometry, ownership, finite quantities, survival costs and class progression can reduce it. Source absence is stronger: no runtime acquisition code references any remaining absent material. Exact rate tuning and detailed biomass rules are design decisions. Scalable mobile homes and core repair remain required goals; the special original-home identity is assessed against that requirement.

Parent runtime lane should validate continuous structural work locality, component identity/mass through normal-access gates, interrupted/disk save behavior, and secondary-shelter service ownership. No old build or fixture is used as proof of current-source runtime success.
