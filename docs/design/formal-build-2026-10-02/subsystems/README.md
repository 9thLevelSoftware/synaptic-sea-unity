# Subsystem specifications

All specifications are draft contracts for review. Foundation acquisition/state details live in the [foundation specification](../../../superpowers/specs/2026-10-02-synaptic-sea-foundation-design.md). These later subsystems inherit REQ-01..08 and the [contracts](../contracts.md), and must receive scoped implementation plans before their product work starts.

| Specification | Work packages | First prerequisites |
| --- | --- | --- |
| [Habitat, topology and mobility](habitat-topology-mobility.md) | H01/H02/H03/M01/M02 | Stable instances/work/save and explicit atmosphere/connector choices |
| [Simulation, survival and biomass](simulation-survival-biomass.md) | S01/S02/S03/S04 | Per-owner state, chosen in-run time/absence rules and resource accounting |
| [Generation and companion release](generation-companion.md) | G01/G02 | Catalog/source graph, typed boundaries and release scope |
| [Presentation and lifecycle](presentation-lifecycle.md) | P01/P02 | Shared gate results and reliable authoritative projections |

Dependency edges and acceptance IDs are authoritative in `work-packages.json`. These specifications define unit responsibilities and failure behavior; they do not invent commitments to optional physics/EVA/crew/network features.
