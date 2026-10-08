# Travel capacity after reduced-quality repair (Phase 1.7a)

Found while planning the guaranteed lifeboat kit: a lifeboat whose power, navigation and propulsion parts are all operational could still refuse travel with `insufficient_propulsion_capacity` when the survivor had repaired them below the required skill.

## Why

`AssemblyMobility.Evaluate` took `rated * propulsion.Health() * power.Health() * hull`. The empty lifeboat weighs 4800 kg against a 6000 kg rating, so the health product had to be at least 0.8 (plus about 0.0167 per 100 kg carried). `ShipSubcomponent.QualityFor` leaves an under-skilled repair at `0.5 + 0.5 * (skill + 1) / (minSkill + 1)`, never below 0.5. One skill-0 repair of `nav_linkage` (min skill 2) leaves 0.667, and a skill-0 `reactor_core` repair (min skill 4) leaves 0.6.

## Measured before the fix

Golden opening state (seed 17, condition Damaged, opening damage applied) and seeds 1 to 200 with condition Damaged; every power, navigation and propulsion part below threshold repaired at the given skill with all parts and tools in hand and no free objective repairs; a 30 kg kit as cargo.

| Repair skill | Seeds refusing travel capacity | Classes at that skill |
| --- | --- | --- |
| 0 | 200 of 200 | cook, communications, field_medic, signal_specialist |
| 1 | 130 of 200 (65%) | medic, pilot, security, salvage_captain |
| 2 | 75 of 200 (37.5%) | scientist |
| 3 | 0 | engineer |
| 4 | 0 | mechanic |

The golden hub at skill 0 also failed with the home objectives' free repairs applied (supported 4000 kg for 4830 kg).

## Fix

`AssemblyMobility.SystemFactor`: `1 - 0.15 * (1 - propulsion * power)`. Worst operational case (product 0.25) supports 5325 kg of a 6000 kg rating. Pristine ships are unchanged (`AssemblyMobilityTests` and `SessionSaveParityTests:176` pins hold). After the fix every row above is 0 refusals.
