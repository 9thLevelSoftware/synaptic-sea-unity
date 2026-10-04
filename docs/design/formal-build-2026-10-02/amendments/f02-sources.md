# F02 concrete source and book proposal

**Selected provisional implementation defaults:** the parent selected F02-A supplies/two racks, F03-A plus paid overhaul, four useful utilities, retained timed study and conservative legacy Keep/Abandon/fully paid Start fresh under user authorization. Numerical amounts, masses, durations, XP, rest thresholds and 0.60 earned condition are tunable hypotheses requiring measured survival and all-class route evidence; they are not owner-approved balance or accepted gameplay. Actual descriptor/physical qualification remains required. Refused migration never deletes, overwrites or replaces original legacy bytes; retain them for later recovery.

**F02-A selected provisionally, not implemented.** Approved solo policy is unchanged. Alternative B remains an unselected design alternative; it cannot silently replace the selected finite route.

## A: deterministic finite home kit plus role-bound derelict supplies

Preserve existing authored starter cache including six hull_sealant. Add a separate reachable container home_service_kit_01 to the actual home gameplay source: wrench x1, scrap_metal x4, wiring_bundle x4, fabrication_schematic_basic x1. The parent qualified the maintenance source/cell and normal gates with a bounded empty-container probe. Exact production descriptor offsets, grounded root/box semantics and authored-row occupancy remain pending; no final kit position is claimed. Do not infer an anchor from a room cell or publish an invented placement ID. The supported default Title/NewRun diagnostic loaded the actual coherent_ship_001 layout/gameplay_slice source. It still does not prove a final authored production container or earned walk. No lock, repaired propulsion, wrench or fabrication prerequisite may block acquisition. F02/F09 must reconcile the qualified actual loader/source/cell, collider/NavMesh, focus priority and LOS before any production data edit. Quantity review is independent of this anchor diagnostic; no kit is currently placed by this task.

New wrench item proposal in tools/tool_definitions.json: id=wrench, category=tool, weight=2 kg, max_stack=1, unbolt/mount capabilities. Existing tools remain required for their actions; tool_wrench is legacy alias only. All11 component carried forms derive name/mass from component_catalog and category=component; no unrelated duplicate mass rows. Unique instances never stack by form.

Register each existing skill_books book_id as a retained manual item, weight0.5 kg, max_stack1, category=book. Recommended study is 30 eligible stationary seconds per book, interruptible, with the manual retained. InventoryPanel offers Study/View manual, dispatched through SessionUiBridge to the owned study job. The player must carry an actual copy, be stationary and able to act; no powered station or fabrication skill is required. Release/pause, movement, losing the copy, injury or invalid actor interrupts; progress resumes explicitly, including after Continue. World hazards/time continue. Study has no industrial -8 stamina drain; ordinary stationary vitals recovery still applies. No instant Use/Use-all consumption or XP burst is offered.

Completion atomically invokes the existing PlayerProgressionState.GrantXpFromBook/BooksRead authority and RecipeKnowledgeState.LearnFromBook exactly once per character_id+book_id, with the same authored book XP/class multipliers. The receipt, BooksRead, knowledge and study job are in one committed bundle; repeated study or duplicate copies produce no XP/knowledge. BooksRead is scoped to that character's PlayerProgressionState, not a profile-wide perk. Display progress, pause reason and Already studied; View remains available. Legacy BooksRead=true seeds mapped book knowledge without awarding XP again; possession/skill XP alone never prove a read. Missing read records retain existing progression and allow ordinary first recorded study. Active study saves progress and loads paused with zero held input. Proposed job/API details and tests are in contracts and the foundation plan.

Primary nozzle mapping remains knowledge_book_id=fabrication_schematic_basic for craft_thruster_nozzle; fabrication4/tier 2/material requirements stay intact. Study teaches the blueprint and existing book XP; it does not bypass the advanced craft gate. Thirty seconds is a proposed action duration, not a global campaign time-scale decision.

| Existing book ID | Finite normal source proposal |
| --- | --- |
| welding_manual_basic | Engineering tool shelf x1 copy |
| diagnostics_field_guide | Engineering service cabinet x1 |
| fabrication_schematic_basic | Home kit x1; later engineering cabinet x1 |
| advanced_welding_schematic | Engineering secured records x1; normal lock/tool path required |
| field_surgery_manual | Medical records x1 |
| pharmacology_formulary | Medical dispensary x1 |
| astrogation_almanac | Bridge chart desk x1 |
| biomatter_signal_analysis | Bridge/science record1; hazard/access path declared |
| scavenger_companion | Cargo service shelf x1 |
| derelict_cookbook | Crew galley shelf x1 |
| leadership_protocol | Bridge records x1 |
| comms_relay_handbook | Bridge communications shelf x1 |

No role may publish the source unless its actual gameplay room/approach exists. `GameplaySliceBuilder.Build` adds finite contents to matched room owners and generates source descriptors/caller references; home source cannot silently come from a fixture helper. Optional advanced-book source absence on a particular ship is reported, not rerolled on revisit.

| Source node per matching role/contact | Proposed finite contents |
| --- | --- |
| engineering_materials_01 | ceramic_plate ×2, coolant_fluid ×1, optical_lens ×1, sensor_array ×1, solenoid_coil ×1, hydraulic_piston ×1 |
| engineering_advanced_01 | graphene_sheet ×1, radiation_shielding ×1, fusion_igniter ×1, cryo_stabilizer ×1 |
| medical_materials_01 | medical_gauze ×3, antiseptic_vial ×2, enzyme_catalyst ×1 |
| crew_materials_01 | synth_fiber ×2, scavenged_protein ×2 |
| synthesis_materials_01 | synthesizer_base ×1, nanite_slurry ×1 |

These17 IDs are the exact audited missing-input set, with coolant externally seeded. Existing normal sources still supply titanium/polymer/adhesive/water/reactive gel and other inputs; the graph must include exact totals for each required chain, not count this table alone as proof. The first eligible engineering contact publishes one protected accessible engineering_materials source; subsequent layouts publish only matching role nodes. Required navigation/first-ship repair must not depend on this contact or advanced fabrication. Generated containers deplete normally and retain unaccepted remainder; contents never refresh by reload/revisit.

Exposure amendment: initially expose recipes with existing complete chains plus recipes whose audited deficits this table and book/tier sources actually close. Each of62 rows is validated individually; rows with a remaining tool/skill/tier/quantity/geometry gap retain explicit defer reason. Do not label all 42 blocked recipes solved by set membership. Tier source remains actual installed component tier; no new free tier 2 station is granted. Direct propulsion salvage remains a meaningful lower-fabrication route with its own normal access witness.

## B: salvage/conversion-first alternative

Home kit is wrench ×1 + fabrication_schematic_basic ×1 only. The F08 practice costs use four existing finite salvageable cargo fittings, each yielding scrap_metal ×1/wiring_bundle ×1 through acquired crowbar work; their anchors and identities are fixed in the same home data amendment. Medical source still carries gauze ×3/antiseptic ×2. Engineering source supplies ceramic ×2/coolant ×1 plus one salvageable sensor assembly; its explicit deconstruction yields optical_lens ×1, sensor_array ×1, solenoid_coil ×1. Advanced/synthesis/crew rows remain as A until separate quantity-conserved conversion recipes are reviewed. This reduces direct loose-material additions but adds actual salvage acquisition work; it must not require wrench dismount to obtain the first wrench.

Both alternatives preserve finite resources and tool/skill class differences. The minimum validation is normal kit acquisition, no-grant first component removal, each book learning/craft gate, medical surgery gauze debit, all 62 row dispositions and seedless-cycle checks. The long-haul economy/rare-source frequency remains later OPEN-09; no statistical guarantee of campaign comfort is inferred.

## Finite recovery sources required with recommended A

Add two authored decommissioned spare-harness racks, home_spare_harness_rack_01 and 02, each containing recoverable scrap_metal x4+wiring_bundle x4. Proposed anchors: maintenance_01 floor_cell_d1_x6_z2, target [24.8, 5.0, 8.0], standing cell[6, 2, 1]; airlock_01 floor_cell_x0_z1, target[0.8, 1.0, 4.0], standing cell[0, 1, 0]. They are cold discarded harness stock, not live utility wiring; recovery cannot disable an essential service. Use acquired crowbar, repair 0, no material input, 8 nominal eligible work seconds per rack, the ordinary work/stamina/rest path, zero XP/training, and a stable once-only recovery receipt. Output remains in a finite searchable rack inventory until accepted; full bags/reload never destroy or refill it. All coordinates require the same physical/focus witness as the kit.

The primary kit plus racks totals12 scrap and 12 wiring. The bounded recovery acceptance spends the primary4+4 on actual ordinary crafting, then recovers the racks:8+8 remains, covering all four utility repairs4+4 and up to four paid legacy overhauls4+4 under the F03 proposal. Separately exercise real reachable spending recipes (e.g. two craft_lockpick_set consume scrap x4; four splice_circuit consume wiring x4 using acquired circuit boards and actual skill/station gates); do not provision deficits to rename that journey a pass. Losing/spending every finite source, destroying tools or choosing lethal hazards is outside this bounded guarantee. Required preflight rejects essential legacy remediation budgets or access that this route cannot actually satisfy, leaving the original save unconverted. No recurring cache refill or infinite XP recovery is introduced.
