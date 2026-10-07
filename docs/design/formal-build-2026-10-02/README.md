# Synaptic Sea formal build program

**Historical record (Phase 0.4, 2026-10-07).** This packet is kept as a record of the Oct-2 audit. `validate_artifacts.py` is no longer a gate: Phase 0.4 deleted dead code that its preservation baseline lists (Infra ledgers, work kernel, `RoomGraphGenerator`, the Rust worldgen seam and others), so strict mode now fails by design. Nothing in CI or `tools/mac` runs it. Do not recapture the baseline. Current decisions live in `docs/design/decisions.md`.

**Status: documentation revision 3, bounded follow-up closure for review.** Audit/authoring baseline 840f14f remains immutable. Prior independently approved a52a347 delivers only bounded F01/F04/F05 corrections; current tier commit 15b09d7 adds only actual station-tier propagation, leaving full F07 incomplete. Full package gameplay acceptance remains pending. C1-C5 are independently cleared; this addendum fixes C6 and selects the reviewed provisional defaults, with tunable numerical hypotheses. This documentation task performs no publication, source/balance edits or save conversion.

The game is persistent survival in space: explore dangerous derelicts, salvage and repair useful equipment, make vessels habitable, weld them into homes, and move a sufficiently powered assembly. Small craft remain useful. Every starting class must be able to establish a home and repair a first ship solo through earned resources and meaningful specialization. Returning and finishing onboarding continue the same life.

## Review order

1. [Master design](../../superpowers/specs/2026-10-02-synaptic-sea-master-design.md): approved requirements, subsystem boundaries, alternatives and success criteria.
2. [Decisions](decisions.md): proposed architectural choices and unresolved product choices, with decision deadlines.
3. [Foundation specification](../../superpowers/specs/2026-10-02-synaptic-sea-foundation-design.md): the first independently reviewable delivery.
4. [Contracts](contracts.md) and [persistence](persistence.md): ownership, interface shapes, interruption and migration rules.
5. [Subsystem specifications](subsystems/README.md): habitat/topology/mobility, survival/simulation/biomass, generation/companion and presentation/lifecycle.
6. [Roadmap](roadmap.md) and [foundation implementation plan](../../superpowers/plans/2026-10-02-synaptic-sea-foundation.md): dependency order, file scopes and acceptance gates.
7. [Evidence](evidence.md) and [acceptance](acceptance.md): what the audit proves and the normal gameplay journeys still required.
8. [Engineering handoff](implementer-handoff.md): F01-F08 dispatch boundaries, dependencies and decisions for the parent coordinator and reviewer.
9. [Bounded revision 3 closure](review-closure-v3.md): C1-C5 review closure, final C6 early-service/runners correction and selected provisional defaults. Final production anchor descriptor/root/occupancy qualification remains pending; narrow tier forwarding is independently delivered.
10. [Prior review amendments](review-amendments.md): responses to six independent contract findings, package gates and staged engineering admission. Concrete proposals: [F02 sources/books](amendments/f02-sources.md), [F03 operability](amendments/f03-operability.md), [F08 finite class routes](amendments/f08-class-routes.md).

## Machine-readable artifacts

| Artifact | Purpose |
| --- | --- |
| `provenance.json` | Verified local revision and separately attributed audit baselines |
| `traceability.json` / `traceability.csv` | Every content, generation and UI finding; additional integrated findings; retained negatives; planned work and test IDs |
| `work-packages.json` | Dependencies, owning subsystem, current files/symbols, proposed files, test IDs and review gates |
| `acceptance-tests.json` | Exact scenario/expected behavior, evidence grade and test placement; all tests planned, not claimed passed |
| `catalog-dispositions.json` | All 62 recipes, exact audited input/gate/source gaps, proposed source disposition and gate |
| `class-bootstrap.json` | All 11 classes and corrected finite-cache diagnostic; no natural-walk promotion |
| `preservation-baseline.json` | Hashes of initially tracked files and existing audit artifacts |
| `concurrent-engineering-scope.json` | Exact separately admitted product paths; observations never overwrite the original baseline or certify implementation |
| `validation-report.json` | Documentation validation and preservation result, separate from game acceptance |
| `artifact-hashes.json` | Exact SHA-256 values for this packet, its specs and plan; excludes itself |
| `validate_artifacts.py` | Read-only documentation/reference/dependency/preservation checker |
| `build_registers.py` | Documentation-only generation of registers from the frozen audit; baseline is never overwritten |

Run `python docs/design/formal-build-2026-10-02/validate_artifacts.py --observe-concurrent-engineering` from this shared checkout. It validates 24 packages, 39 planned tests, 62 trace rows, 62 recipes and 11 class records, and compares 3,264 original protected hashes while recording exact separately admitted implementation/evidence paths. Strict mode rejects baseline changes. Documentation validation does not rerun or certify product tests/campaign acceptance.

`--report --write-hashes` refreshes only the report and artifact manifest after documentation checks succeed. The report retains a separate preservation result: a concurrent unadmitted change still makes the overall check fail and is never hidden by a valid documentation manifest. `--verify-hashes` performs a separate read-only final manifest verification. Do not regenerate the authoring preservation baseline from the shared worktree.

## Review boundary

The written designs and plan are submitted together as requested. Reviewed foundation defaults are selected provisionally under the user authorization; numerical tuning and gameplay acceptance need measured evidence. Broader explicitly open product choices remain unresolved. The next implementation scope is the foundation only. Later stages require their own scoped implementation plans after their decisions are settled. Existing `docs/design` delivery notes and the deep audit remain intact as historical evidence.

This planning task writes documentation only. It does not change or revert product code, balance data, saves, proprietary assets, existing audits or Git references. Separately authorized engineering changes are recorded by path/hash in the validation report. No commits, pushes, PRs, merges, deployments, companion builds or creature imports are performed by this task.
