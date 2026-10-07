# Save versions, transactions and recovery

**Status: proposed.** Current `SaveMigrationService.TargetVersion` is `gate2-current-run-6`; `WorldTargetVersion` and `WorldSnapshot.WorldSliceVersion` are `world-4`. Existing world4 migration also handles embedded home snapshots. `RunSnapshot` comments require an ADR for new fields: SS-ADR-03/06/08 propose the additions below. Existing v1..v6 chains and Godot compatibility remain intact.

## Version and compatibility contract

| Change | Proposed schema | Owner and compatibility |
| --- | --- | --- |
| Equipment instances, paused work tickets, knowledge and commit-generation reference | `gate2-current-run-7` | `RunSnapshot`, assembler and migration. Optional legacy defaults cloned, not written back on read |
| Per-ship instances/remainders, generation binding and service-state extension envelope | `world-5` | `WorldSnapshot` and per-ship summary adapters; exact layout/profile/seed retained |
| Equipment/holder payload | `component-instances-1` | Unique IDs, condition/mass, source/holder revisions and provenance certainty |
| Work payload | `work-transactions-1` | Stable owner/target, progress, declared material policy, paused restore and committed effect IDs |
| Knowledge payload | `recipe-knowledge-1` | Starter seed plus learned book/codex/dismantle events; no nullable unrestricted state |
| Storage generation manifest | `save-commit-1` | Run/slot/generation/parent and complete payload hashes/schema identity |
| Future local services/topology/time | Independently versioned nested extensions | Append pure migrations; no silent repurposing of world5 fields; later top-level bump if required |

Implementation reserves the proposed versions after checking current HEAD for intervening changes. Newer unknown versions are read-only rejected. Migration may accept documented absent old fields; present malformed/nonfinite fields reject. The original bytes and prior generation remain available. Seed precision follows the existing decimal-string creature identity rule. Content/generator/library versions are separate from save schema.

## Durable local commit

Proposed implementation uses a staged generation directory/record under the selected disposable or authorized profile root. The exact filename convention is internal; paths must be relative to that root and reject traversal.

1. Capture one immutable committed world generation on the simulation thread. Freeze only snapshot assembly, not gameplay across disk latency. Run/world/layout references share `generation_id` and authoritative owner revisions.
2. Serialize to new staging payloads. Validate schemas, IDs, finite values, content references, hashes and run/slot identity. Flush writes using a platform-supported durable mechanism verified during implementation.
3. Write and durably publish an immutable complete generation manifest. Do not expose unvalidated or half-written generations through Continue.
4. Replace/publish the small active-generation pointer with a supported recoverable replacement; keep the old pointer/parent generation. `FileSystemStorage.WriteText` must no longer delete the only good destination before replacement. Verify Windows behaviors through actual filesystem probes; no untested cross-platform atomicity claim.
5. Update the index as a reconstructible projection. Failure here preserves the committed generation and triggers index reconciliation, not payload deletion.
6. Retain at least the preceding valid generation until the new one has been reopened/validated. Cleanup is separate, scoped to unreferenced owned staging paths and never deletes frozen or user-named saves blindly.

A work/item command and its receipt enter the same snapshot. Storage failures return an explicit save error, with the previous committed generation still loadable. Replaying a committed receipt cannot mint output or XP. Full historical event logs are unnecessary; retain receipts referenced by active snapshots/jobs and prune only after no supported snapshot can need them.

## Recovery state table

| Disk state | Load/recovery result |
| --- | --- |
| Valid pointer, matching complete generation | Load it; verify identity/hash/schema before live-state apply |
| Interrupted staging write; old pointer valid | Ignore incomplete staging, retain old generation; report failed save |
| Valid complete staged generation, pointer absent | Validate all payloads/references and promote using recovery policy; if more than one branch is ambiguous, present candidates rather than guessing |
| New pointer corrupt, valid parent/backup | Recover the matching valid prior generation with visible recovery notice |
| Valid world but mismatched run generation | Reject the combination; search matching generation/parent; do not splice inventories/loot into a newer world |
| Index missing/corrupt but valid manifests | Rebuild index from manifests scoped to that run; do not recreate data from metadata |
| Lone legacy temp plus absent destination | Validate legacy slot/run ID, schema and referenced layout/world; recover only a complete consistent legacy set; staged audit case becomes a real regression |
| Unrecognized version/content/library | Retain bytes and return version/missing-content reason; no reroll, downgrade or auto-new-run |
| Locked destination/disk full | Previous good generation remains; no success caption and no index pointing at a missing payload |
| Migration interrupted | Original save unchanged; migration staging restart is idempotent |

## Legacy state rules

Mounted components keep their original instance and saved condition. A dismounted slot and anonymous carried form reconcile only with an unambiguous matching witness; duplicate/ambiguous links preserve original data. Anonymous quantities become stable legacy_unknown records with known mass and condition_state=unknown, condition=null. F06 must also deliver actual Inspect/Overhaul UI, passive mechanical surface and paid raw 1+1/wrench/repair 0/30-nominal-second result 0.60 described in f03-operability.md. It is newly achieved rehabilitation, not guessed previous health. Before publishing conversion, validate independently safe reachable tools/surface/material budget for essential equipment; if absent, return legacy_resolution_unreachable and leave original bytes/parent/live world untouched. Unknown cargo serialization alone cannot certify migration.

Existing home/boat machinery aliases are migrated into a single known owner; never sum aliased managers as two engines. Saved mobility specifications remain authoritative; malformed coordinates/graph records reject before mutation. Current slot condition negatives are retained. The repeated minimum 0.55 remount effect is not reissued merely on load.

Active manual/study/overhaul work becomes paused_restore with zero held input. Completion receipts plus applied outputs/read/condition outcomes are checked before resume. New paid craft receipts remain paid without recharging. Legacy active/queued craft without proof becomes paused_unverified regardless of matching station/progress/quality: the old auto-queue path did not debit inputs. Preserve original records; actual RecipePickerPanel offers Keep paused/Abandon without output or refund/Start fresh through new gates and full payment/time. Never infer legacy_paid. Knowledge seeds declared starters and preserves existing unlocks; legacy BooksRead=true grants mapped knowledge with no XP replay, while possession alone proves nothing. Retained manual study progress/read receipts survive Continue.

Legacy top-level home hydroponics/water state belongs only to the original home station IDs. H01 moves it into owner-scoped service state through a versioned extension; new claimed vessels receive no cloned crop inventory, job timers or free station assets. `HomeSpawnSafety` stays onboarding policy and is never automatically copied to every habitat.

## Fault and conservation matrix

### Complete payload assembly for every slot family

Proposed `Core/Session/SavePayloadAssembler.Build(RunSession, string slotId, string slotKind)` captures one DomainTransactionCoordinator revision into: active RunSnapshot, complete WorldSnapshot (including its original-home slice and all visited ships/loot/cargo/instances/jobs/receipts), immutable referenced layout/profile/slice artifacts or verified existing hashes, owner/local player pose, run/slot metadata and generation manifest. Duplicate serialized summaries must agree with the same committed owners/revision. Missing layout/world references reject before publication. Immutable layouts may be shared by hash; every referenced byte set must be available and manifest-bound.

| Gameplay caller / family | Bundle and restore contract |
| --- | --- |
| RunSession.AutoSaveCurrentRun / RequestSave -> world checkpoint | SavePayloadAssembler for world slot; full bound run+world+layouts, current location/pose; Continue restores the exact generation |
| RunSession.TickAutosavePolicy -> autosave_a/b/c; SaveLoadService.SaveCurrentRun -> autosave_active/current_run.json | Each rotation/pointer references its own complete generation; never attach a run snapshot to latest world.json by convention |
| RunSession.RequestQuicksave -> quicksave | Same complete bundle with quick metadata/cooldown; load restores its world generation, not newer visited state |
| SaveSlotScreenModel.SnapshotBuilder/ConfirmSaveToSlot -> slot_01..06 | Replace the single-run builder callback with complete payload request through session; manual slots bind full run/world/layout generation |
| SessionUiBridge subscription to MenuCoordinator.SlotSnapshotLoaded / RunSession.ApplyManualSlot | New-schema load uses a validated complete bundle and temporary-world apply. Restores captured player/ships/loot/cargo/condition/jobs/time/graphs as one generation, subject to current death gate; never splice into newer world |

A new-schema manual load rewinds the captured in-run world consistently. It does not roll back external user preferences, achievements or profile/meta authorities that currently live outside the run. This proposal changes the partial manual-slot seam deliberately to eliminate duplicate/mixed world state; test it through the actual menu. Old standalone RunSnapshot slots retain documented legacy partial restore only when their references and run identity are valid, label that limit and never fabricate a matching full world. If safety/identity is ambiguous, retain bytes and reject with `legacy_world_binding_missing` rather than invent data.

SaveLoadService low-level standalone serialization APIs may remain for old fixtures/import paths, but production gameplay calls go through full bundle assembly. Do not require F05 unit tests to construct F06 instances or F08 routes: F05 fixtures use existing-schema complete plain payloads; F06 then integrates new fields.

Full F05 depends on F03's committed DomainBundle/revision. Its pure storage/recovery portion can use supplied immutable plain payloads and proceed independently; SavePayloadAssembler and actual gameplay slot integration wait for that F03 owner. F06 then integrates schemas/resolution. A plain-payload disk pass cannot close complete gameplay assembly.

### Frozen-run and terminal authority

Existing SaveLoadService.FreezeRun, PermadeathResolver.HasDiedIn/RecordDeath and TitleSaveQuery/SaveSlotScreenModel gates are authoritative compatibility behavior. Recovery checks death/freeze authority before looking for any candidate or parent generation. A readable parent from a dead run remains nonplayable; index rebuild must retain frozen/read-only presentation and must not call ClearDeath. Recovery failure is not permission to start a live copy of a frozen run.

New saves additionally bind run-level terminal state/tombstone to run_id independently of a corruptible slot index, supplementing existing per-slot death records. Freeze publishes durable terminal authority before reporting terminal success. Existing death records are preserved and consulted even when manifests/index are rebuilt. Missing/ambiguous association of a legacy death record to a recovered candidate fails closed. New-run slot reuse may clear the prior run's stale slot death only through the existing explicit reclaim-on-successful-new-run-write behavior; ancestor recovery never invokes that path.

Tests corrupt/delete index after FreezeRun, stage a valid predeath parent/temp, recover every world/active/rotating/quick/manual family and assert no playable resurrection, no tombstone clearing and preserved epitaph. Also test intentional live new-run slot reuse remains compatible. No survivor/successor-world policy is chosen.

### Unknown condition and paid inputs

Legacy unknown equipment serializes condition_state=unknown, condition=null with stable migrated ID/catalog mass/holder. Known equipment is finite[0, 1]. Missing evidence never becomes zero, 0.55 or pristine. Inspect can restore an exact saved witness; otherwise the reviewed same-delivery paid overhaul creates a newly achieved0.60 with old null/provenance preserved in its receipt. Actual UI, finite acquisition, eligible work/rest/interruption, input debit/result-once and essential unreachable migration refusal are mandatory. ApplySummary distinguishes JSON null from numeric0 and rejects mismatched/nonfinite tags. Detach/mount/load preserve machinery health and only change availability.

New paid craft receipts, owner/station/recipe hash, exact consumed set, start quality and completion receipt share the domain snapshot; restore never consumes a second set. Legacy active/queue records without proof retain paused_unverified payment state, original shape/source hash and player disposition/archive receipt. Test historical BeginCraft versus unpaid queue records with identical shapes, including status-complete records: neither can auto-output/refund. Explicit Start fresh creates a separately paid/time-complete job and acknowledges possible old expenditure. Unpaid/contradictory new records reject; archival decisions persist once.

Use `MemoryStorage` for logic and a new disposable filesystem root for durability. Inject failure before staging, during each payload write, after validation, before/after manifest publication, before/after pointer publication and during index update. Restart through real `SaveLoadService`/coordinator at every point. Assert an old or new complete generation, never a mixed one; instance uniqueness, condition, finite container remainders, XP/output-once and paused work remain conserved.

Repeat for every known legacy run/world version, manual/autosave slot identity, malformed/future versions, file locks and migration interruption. Do not touch player profiles or old audit disposable saves. Cloud conflict/synchronization is not asserted by local recovery; defer provider-specific contracts until cloud integration is an approved work package.
