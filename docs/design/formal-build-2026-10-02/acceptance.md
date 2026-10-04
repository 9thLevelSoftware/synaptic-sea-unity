# Acceptance protocol

All `acceptance-tests.json` entries are **planned**. Each names an exact scenario, expected outcomes, owning work, target test path and evidence scope. Existing test files are anchors; new test names are proposed. No new product tests are written or executed in this design stage.

## Evidence lanes

1. **Catalog/static:** same merged loaders, actual producer callers and production exposure manifests. Proves graph consistency; finite route solving includes quantities/XP/order but still needs physical witness.
2. **Provisioned diagnostic:** isolate a component at condition 0.23, tier 2 station, explicit work target, claimed vessel or installed engine. Record every provisioned field and purpose. Cannot close normal-acquisition gates.
3. **Instantiated fixture:** actual collider/NavMesh/HUD/audio objects. If doors opened/tools supplied, report that boundary. Does not establish normal source access or native control.
4. **Normal earned journey:** fresh supported Title launch, physically obtain tools/cache/parts, use normal gameplay input paths, survive/recover, save/Continue and revisit. No grants, health replenishment, bypassed locks or arbitrary hazards.
5. **Native journey:** explicit real keyboard/mouse/gamepad plus exact development player source/build identity. Batch Play fixtures are a different lane. Target hardware and duration require OPEN-11/09.

## All-class solo gate

Run engineer, mechanic, medic, pilot, scientist, cook, security and communications through fresh available Title choices with no hub repair/XP perks. Establish legitimate class-unlock provenance separately for salvage_captain/field_medic/signal_specialist; fresh-profile availability remains unchanged. Directly configured locked classes run finite route diagnostics but cannot alone pass Title acceptance.

For each class record finite cache quantities, held tools, real skill/fractional XP, objective order/force-repair side effects, work/stamina/health costs and denials. Achieve safe usable shelter and a repaired/powered/controlled first craft that can leave and return. Save mid-work and release input, Continue paused, finish explicitly; preserve component condition/identity. Run common early objective orders and compare security's battery/star-chart-first discriminator. A hidden optimum order cannot be the sole viable policy route.

## Meaningful repeated expeditions

After foundation, L01 requires three distinct normal expeditions as a minimum behavioral discriminator, not a long-haul duration commitment. Trip1 acquires care/repair/food-water supplies; trip2 reclaims/adapts a hull or obtains propulsion/station machinery; trip3 revisits depleted/modified state and explores a new contact with real resupply. Include an adverse interruption, injury/treatment/recovery, local power outage, load/cargo tradeoff, save inside secondary shelter and departure/return with a small craft. Onboarding/return never freezes survival or deletes active saves. Costs/depletion are ledgered, renewable outputs proven through actual station sources.

The native campaign review then selects a duration/hardware budget through OPEN-09/11, profiles loaded hull/AI/CPU/GPU separately and tests chosen balance. Passing three short trips cannot certify a months-long economy. If earned propulsion or natural reclamation fails, preserve the failure and diagnostic evidence; do not replenish/grant or weaken survival to rename the same run a pass.

## Commands for later implementation

Core discovery: `dotnet test tools/dotnet/SynapticSea.Core.Tests --list-tests --filter FullyQualifiedName~<owning test class>`. Verify all intended cases and a nonzero count, then execute with `--nologo --filter FullyQualifiedName~<owning test class> --results-directory <new evidence directory>`. Aggregate Core: `pwsh tools/test.ps1 -Mode Dotnet`. Tests/EditMode belongs to this Core project; Tests/EditModeUnity belongs to Unity assembly SynapticSea.Tests.EditModeUnity, and Tests/PlayMode to SynapticSea.Tests.PlayMode. A Core filter cannot run either Unity assembly.

Focused Unity must use the actual Unity runner with matching EditMode/PlayMode and full fixture filter, exclusive project access, a new result XML/profile path and hidden process launch. Confirm the XML contains the intended full fixture/case names and nonzero executed count; zero discovery or skipped-only is not PASS. Existing tools/test.ps1 runs Core first even for Unity modes and overwrites generic logs. Use a scoped direct runner or a dedicated documented harness to preserve audit evidence. Runner/discovery fields are also recorded per acceptance-test row.

F03 package exits prove model conservation, atomic faults and actual UI commands in A03/A04/A31/A32; F06 adds their saved Continue/revisit integration. F06 uses legacy/provisioned schemas and complete slots, without making downstream F08/F09 earned class journeys its exit gate. A33's phase unit and saved paid receipt discriminate the exact-one-input-set case. Provisioned disk/slot fixtures cannot close native or normal acquisition acceptance.

Package CI Unity jobs are manual workflow_dispatch; this program does not change that policy. Product validators integrate into the existing license-free Core merge gate after implementation. Documentation checker is separate: `python docs/design/formal-build-2026-10-02/validate_artifacts.py --observe-concurrent-engineering` for the shared checkout.

Revision 3 adds A35 legacy payment reconciliation, A36 reachable unknown resolution/preflight, A37 genuine utility/raw/effort/rest/finite recovery, and A38 retained timed study. Their actual UI/normal acquisition portions remain F09. Concrete Core class-route discovery+execution, PaidCraftStateTests creation and scoped actual Unity CLI examples are in the foundation plan; 39 tests are planned and are distinct from the approved narrow engineering results.

C6 adds A39 early work/receipt units. A37 requires both Core HomeUtilityRepairTests and Unity PlayMode AuxiliaryUtilityViewPlayModeTests; A38 requires Core ManualStudyTests plus Unity EditModeUnity ManualStudyPanelTests. Exact paths, scoped runners, expected methods and nonzero/PASSED XML assertions are in the foundation plan and additional_targets records. Selection of tunable defaults never promotes these planned/provisioned checks to earned survival acceptance.
