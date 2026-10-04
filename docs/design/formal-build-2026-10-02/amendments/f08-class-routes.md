# F08 paid utility repair and finite solo route proposal

**Selected provisional implementation defaults:** the parent selected F02-A supplies/two racks, F03-A plus paid overhaul, four useful utilities, retained timed study and conservative legacy Keep/Abandon/fully paid Start fresh under user authorization. Numerical amounts, masses, durations, XP, rest thresholds and 0.60 earned condition are tunable hypotheses requiring measured survival and all-class route evidence; they are not owner-approved balance or accepted gameplay. Actual descriptor/physical qualification remains required. Refused migration never deletes, overwrites or replaces original legacy bytes; retain them for later recovery.

**Revision 3, selected provisional utilities/recovery; no product edits by this task.** Every starting class's solo viability is approved. F02-A finite sources and F03-A separate-health cap are selected provisional defaults. This amendment replaces unnamed XP panels with four actual auxiliary hardware defects. Work/XP values and recovery budgets are selected tunable hypotheses; actual physical positions still require qualification and all-class survival evidence.

## Hardware owner, effects and anchors

Proposed Core/Systems/ShipSystems/ShipAuxiliaryUtilityState owns ship-qualified fixture ID, hardware_ready, revision, work progress and per-character training receipt. ShipInstance.GetSummary/ApplySummary and the original-home snapshot bind that state to HomeShip; it is part of DomainTransactionCoordinator's bundle. It is separate from ShipSystemsManager.Systems: objective ForceRepairSystem/RepairSubcomponent and restore_systems cannot erase these defects or award their XP. No default spawn on revisit can reset them. Legacy saves without these new fixtures preserve existing service behavior and receive no retroactive repair/XP grants.

| Stable fixture/action target | Real effect of repair | Proposed anchor in the actual coherent_ship_001 layout |
| --- | --- | --- |
| maintenance_fabricator_feed_01: loose fabrication-console supply isolator | Restores the existing home fabricator station's local feed. Powered = hardware_ready AND existing local station power. No tier, machinery-health or free output change; the passive compatibility overhaul surface is independent | maintenance_01, floor_cell_d1_x5_z2, standing cell[5, 2, 1], target[20, 5.2, 8.8] |
| maintenance_cargo_relay_01: failed cargo-controller service relay | Restores CargoHoldControl's powered bulk deposit/withdraw command. Existing item-by-item physical cargo InventoryPanel transfer remains possible through its manual access handle; repair restores convenience, not a resource grant | maintenance_01, floor_cell_d1_x5_z1, standing cell[5, 1, 1], target[20.8, 5.2, 4.0] |
| medbay_task_light_01: disconnected medical work-light feed | Restores the actual medbay task lamp through the runtime fixture view, making treatment/study surfaces readable. Portable treatment and ambient-lit manual study remain usable; no healing/atmosphere bonus | medbay_01, floor_cell_d1_x7_z-1, standing cell[7,-1, 1], target[28, 5.2,-4.8] |
| airlock_dock_beacon_01: failed dock-status repeater and approach light | Restores an actual approach lamp/repeater displaying current DockingManager/airlock state. It cannot unlock a door, generate thrust or falsify status | airlock_01, floor_cell_x1_z1, standing cell[1, 1, 0], target[4.8, 1.2, 4.0] |

All coordinates are ship-local proposals derived from existing 4 m floor cells (deck 1 floorY 4, deck 0 floorY 0). They are not clearance evidence. Proposed Runtime/Session/Views/AuxiliaryUtilityView projects committed utility and actual power/dock state into visible fixture lamps/feedback. A unit test that flips an unused flag cannot pass: test each named consumer plus instantiated on/off view. F02/F08/F09 must validate collision/NavMesh, approach/LOS, focus priority near existing cache/objectives and actual manual cargo handle, recording the final placements. A failure blocks that fixture's acceptance; it cannot be silently relocated or replaced by an invisible XP target.

## Dedicated adapter, costs and completion

Use proposed Core/Session/Interactables/AuxiliaryServicePoint.cs and RunSession.Build/Interact/WorkAction composition, registered in InteractionRegistry with doc parity. This is not an unmodified RepairPoint: current RepairPoint requires a nonfunctional ShipSubcomponent, accepts required IDs from part-category inventory, uses its own channel, and grants 25 repair XP. wiring_bundle is raw. Do not alter its existing 25 XP behavior or relabel raw items globally to reuse it.

The dedicated action repair_auxiliary_utility requires the fixture's hardware_ready=false, acquired crowbar, repair 0, strict distance<1.8 m and direct LOS to the stable owner-qualified target. It checks InventoryState.GetQuantity(scrap_metal)>=1 and GetQuantity(wiring_bundle)>=1 at begin/resume/commit regardless of category. No remote cargo borrowing. No powered-service dependency may block fixing the feed itself.

Each repair costs scrap x1+wiring x1, 12 nominal eligible work seconds and direct repairXP60 through PlayerProgressionState.GrantXp("repair", 60, false) on the staged progression. Current technical multipliers/fractional carry remain. Four repairs cost 4+4 and 48 nominal work seconds; 48 is not a wall-clock promise. DomainTransactionCoordinator atomically debits exact raw counts, sets the hardware state, stores a completion/training receipt and publishes XP once. No generic EmitTrainingEvent or RepairPoint.Complete call can issue another award. Proposed TrainingEventBus.RecordApplied records the already-applied event without granting XP; restore/replay skips receipt-owned already-applied records, rather than replaying the entire log into restored totals.

Eligibility and timing follow the ordinary WorkActionDriver/RunSession.TickWorkAction effort path: wound speed and clamp(0.35+0.65*stamina/max, 0.35, 1) scale progress; 8 stamina per eligible elapsed second is charged once, with the final elapsed slice bounded to remaining work. Normal VitalsState.Tick still handles stationary recovery/hunger/thirst/hazards; do not double-tick it or assume zero net stamina cost. Released hold pauses with no industrial drain/progress/noise; toggle stays physically eligible. Movement/LOS/owner/tool loss, damage or exhaustion pauses/interrupts with the actual reason. Inputs are not escrowed/debited until final commit. Bounded progress is saved and explicitly resumed after valid rechecks; Continue restores zero held input. A target already repaired cannot finish/pay/award again.

Ordinary rest is release/pause, stop moving in actual safe shelter, eat/drink/treat as needed, and wait for existing stamina recovery; proposed route policy pauses below 25 stamina and resumes at 75. No rest hotkey heals or refills supplies. Tests measure actual vitals, elapsed time and survival costs, including hunger/wound recovery modifiers and difficulty. If current supported fresh-start settings cannot support this work/rest route with actual resources, report it and amend the values before acceptance.

If real later damage re-breaks a utility, paid repair may restore it but the character+fixture training receipt suppresses repeated XP. Revisit/reload/remount/player-induced repeated damage cannot farm the initial award. Freshly generated unrelated fixtures are not automatically part of this four-job bootstrap budget.

## Class budget and objective ordering

| Class | Technical multiplier | Four repairs effective XP | Corrected finite-cache deficit to repair 2 |
| --- | ---: | ---: | ---: |
| engineer |1.5|360|0|
| mechanic |1.5|360|0|
| medic |0.7|168|35.5|
| pilot |1.0|240|0|
| scientist |1.2|288|0|
| cook |0.8|192|152|
| security |0.9|216|0|
| communications |0.9|216|133.5|
| salvage_captain |1.1|264|0|
| field_medic |0.8|192|152|
| signal_specialist |1.0|240|115|

This is proposed arithmetic, not an executed route. Even without star_charts practice, cook/field_medic's two actual objective events 96 + actual cargo seal12 + service repairs192 total 300, equal to repair 0->1 cost 100 plus 1->2 cost 200. That reduced route has zero arithmetic margin and must pass real fractional/wrapper tests. Existing cargo seal15 base XP and six hull_sealant remain. No unused authored xp_event, fire farming, expiry timer or fresh meta perk is substituted. The selected utility owner is outside objective force-repair mappings, so restoring systems early cannot erase its remaining hardware work; common early objective orders remain acceptance cases.

F02-A adds two finite accessible spare-harness recovery racks, each scrap x4+wiring x4, acquired with crowbar/repair 0/8 nominal eligible seconds/no input/no XP. Their passive accessibility is independent of the broken powered utilities. After ordinary use of the kit's4+4, recovering8+8 can fund all utility repairs4+4 and up to four compatibility overhauls4+4. Preserve accepted-count/remainder/receipt semantics. Test actual legal material-spending recipes and kit/rack acquisition, interruption, rest and save/revisit. This guarantees a bounded viable recovery route, not recovery after spending/destroying all 12+12, discarding tools or every lethal choice. Finite resources remain consequential.

## Methods and acceptance boundary

Optional class presentation keeps the same actual hardware/effects/costs: engineering harness alignment, medic safety checks, cook sealed service cartridges, security housing braces, communications signal tracing and pilot/scientist/salvage calibration. Presentation alone is not a claim of different skill mechanics. Existing classes/advanced tools/repair/fabrication gates remain; technical multipliers change effort needed before the first ship. Larger specialty mechanics require separate quantified changes.

F08 exit: real consumer/effect tests, raw quantity and exact XP/log-once tests, finite all-class/objective-order model and serialized progress/receipts. F09: fresh Title acquisition/normal input/work-rest/first-ship journeys, plus legitimate unlock provenance for the three locked classes. No provisioned fixture can close those earned gates. Legacy utility compatibility and paid unknown resolution are implemented/tested before committing F06; F08 cannot be used as its missing action dependency.
