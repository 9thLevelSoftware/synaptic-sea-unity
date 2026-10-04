# Versioned world generation and companion release

**Status:** proposed G01/G02; production release scope remains OPEN-13. The approved crawler is still approved. This specification does not request redundant approval or modify companion assets.

## G01: opportunities that connect to gameplay

Retain `ShipGenerator`, `ShipLayoutGenerator`, `GameplaySliceBuilder`, `FirstRunAwayGate`, constrained/reclamation profiles, physical standing-space/portal contracts and `RuntimeVisualCatalog`. Saved profile/version/seed/layout identity remains immutable. New generation rules get a new profile version and migrations of mutable overlays only when possible; never reroll visited ships.

Proposed `Core/Procgen/GameplayOpportunityValidator.cs`: `GdDict Validate(GdDict layout, GdDict catalog, GdDict sourceGraph)` combines logical connectivity with registered source opportunities and interaction anchors. It checks every published producer against a compatible item/tool, required reachable station/skill/book path, finite contents and persistence owner. Fixture-only grants/opened doors are tagged and excluded from production source proof.

Model graph validation cannot certify collider aperture. Instantiated checks include furnishing, damage/locked portals, vertical traversal and actual standing approach to supplies/equipment. Rich v4 is currently size1/2 single-deck; legacy has multideck coverage. Do not claim rich multideck readiness from legacy graph tests. Retain explicit fallback diagnostics and FirstRunAwayGate fail-closed rejection.

Production validator layers: catalog/IDs -> registered producer source graph -> seeded finite route -> serialized layout determinism -> instantiated navigation/focus -> live player-camera/HUD -> native/repeated expeditions. Reuse existing structural/prop validators and actual kit binding tests instead of creating a parallel asset registry. A24 covers selected seeds/families plus exact physical edit cases; it records actual `program_id`, not discarded CSV `actual_seed`.

## G02: approved asset identity through runtime

Local companion `D:/critter-creator` is `00735856e4d06e5823608e0ab77832a992baf098`; embedded `com.ninthlevelsoftware.crittercrafter` is `01230d84c895c13fdb744df98d5b39724c35ba44`. No package update is silently required; compatibility is tested against a selected production library manifest.

The exact `crawler_alien_tripod_balanced_v3` owner approval from 2026-09-30 is retained. Its current compiled content hash in the audit is `85c099c2404b6ad5170d4adda08c2aac7f2157789212045c4202b7c66286de2c`. An older stale receipt does not revoke owner approval. Source approval, receipt validity, geometry/source freshness, compiled decision freshness, archive identity and installed game registration are separate states.

In companion `src/critter_crafter/library/commands.py`, `_built_catalog` and `library_pack` currently may reuse old compiled status after normal approve/reject without an explicit build. Proposed minimal change refuses decision mismatch with an actionable rebuild instruction or applies a verified current decision overlay; choose the former initially to simplify release provenance. Preserve expensive unchanged geometry reuse. Test both skeleton/part approve/reject -> pack transitions in a disposable copied fixture, never mutate the real approved crawler to test revocation.

Selected production release manifest records companion source SHA, package version, library ID/version, catalog/source/compiled decision hashes, artifact/archive hashes, owner decision references and the game's source/build identity. Game `RunSessionHost` assigned library or `Resources.Load<CritterLibrary>("CritterProductionLibrary")` must resolve the chosen installed library. No such production asset is present in the audited tree. Installation is later product/asset work after OPEN-13 selection and design review, not accomplished by this documentation task.

`ThreatCreatureFactory` continues exact persisted recipe/pool/library/version/seed identity and refusal to reroll malformed/incompatible saved data. Mechanical and unsupported pools keep explicit fallback. Adding a library later must not silently regenerate old empty-placeholder identities; use an explicit future compatibility policy or new encounter identities. Imported creature colliders stay disabled; game hitbox/navigation owns gameplay.

A25 requires actual library registration, a normal new encounter, instantiated rendering/controller/bindings and save/restore recipe identity. A26 is companion release decision-delta/freshness validation, isolated from normal game acceptance. Documentation updates correct `docs/critter-runtime-integration.md` readiness statements separately from actual import. No push, PR, companion build or import is authorized in this design stage.
