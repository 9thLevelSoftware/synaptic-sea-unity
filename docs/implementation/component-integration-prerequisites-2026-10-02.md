# Component integration prerequisite evidence — 2026-10-02

The detached F04A work and record-only training primitives and the unused supplied-payload F05 generation store are implemented and verified. Live component conversion remains gated.

Baseline is `b75c137dcb5d58ffaea728263cb38577aae24fd5` on local branch `codex/foundation-bounded-2026-10-02` in the existing isolated checkout. Three engineers owned non-overlapping product paths; the coordinator alone ran tests, Unity and Git. Fresh independent reviewers inspected actual source and preserved failure assertions. Original checkout, saves and assets were not targets of the task. Concurrent design/audit/spec documents were read without editing or staging them.

The [deep audit](../audit-deep/README.md), [runtime diagnostic findings](../audit-deep/runtime/results-v7/model-results.json), [handoff](../design/formal-build-2026-10-02/implementer-handoff.md), [contracts](../design/formal-build-2026-10-02/contracts.md), [persistence contract](../design/formal-build-2026-10-02/persistence.md) and [foundation plan](../superpowers/plans/2026-10-02-synaptic-sea-foundation.md) supply the reviewed boundary. A39 is primitive evidence; this note adds no scripted gameplay slice.

## Implemented behavior

`WorkEligibility`, `WorkTransactionState` and `IWorkCommitPort` validate explicit actor/ship/target identity, revision, locality, range, input, tools, materials, skill, power and definition-owned effort policy. Released hold produces zero progress/debit; restored nonterminal jobs require explicit eligible resume. Progress and effort recommendations remain detached from live vitals/materials. Work delegates publication through the injected port, retains its prepared transaction ID on ambiguous outcomes, retries that same ID and remembers confirmed results without another effect. Import is defensive and preserves committed history. Fresh review reproduced context changing inside Prepare; the corrected kernel revalidates immediately before Commit. No concrete live publisher or session adapter was added.

`TrainingEventBus.RecordApplied` records validated resolved receipt-owned events without Emit, filters, XP grants, callbacks or delivered/drop-counter changes. Receipt-owned replay cannot grant again. Summary import validates privately and retains every confirmed canonical receipt; omission or self-consistent rewriting rejects before replacement. Sequence alone is excluded from receipt comparison. Existing ordinary Emit/replay behavior remains covered. The receipt-local validated integral long/double XP seam does not change global Variant or the typed component/work boundary.

`SaveCommitCoordinator` validates explicitly supplied exact current run6/world4 text plus every declared referenced layout/slice/kit, binding, owner revision and compatibility stamp. It publishes immutable payloads and manifest, then a shared slot pointer, then a reconstructible index. Recovery validates complete ancestry and reports ambiguity rather than selecting by timestamp; corrupt active bytes can retain a verified previous selection. Shared-slot ownership includes a readable previous pointer when active is absent. Strict identifier types, present-lifeboat blueprint, kit module/grid closure and explicit terminal authority/tombstones fail closed. Every playable return path rechecks lifecycle, including postpublication errors and exact repeated commits. A verified disk commit stays reported as committed even when terminal state forbids returning its payloads. The service has no gameplay caller, legacy writer, migration, cleanup or implicit slot reclaim.

Unity exposed an unusably deep initial path hierarchy: its native fixtures reached 462-character artifact paths and failed before initial publication. The corrected unused layout retains full SHA256 over explicitly framed UTF8 run/slot/generation identities, uses sorted artifact ordinals validated before payload reads, and isolates foreign generations through strict typed headers and tuple tokens. Actual Windows paths reserve staging/backup/directory budgets before writes; unsupported roots return `path_budget_exceeded`. Existing Storage and legacy paths are unchanged. The original native labels were retained; this was a coordinator correction, not a shorter fixture workaround. The [frozen path amendment](../../artifacts/component-integration-2026-10-02/storage-path-proposal.md) records the exact mapping and [Microsoft path limits](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation) supporting the diagnosis.

## Recorded verification

Raw logs, TRX/XML, commands, source inventories, assemblies, full failure packages, native storage roots and reviews remain under [the implementation artifacts](../../artifacts/component-integration-2026-10-02/). The [machine verification](component-integration-prerequisites-2026-10-02-verification.json) records exact product commits and final source matching.

| Phase | Discovered | Passed | Failed | Skipped | Meaning |
| --- | ---: | ---: | ---: | ---: | --- |
| Training scaffold RED | 75 | 1 | 74 | 0 | Compiling empty new API; ordinary replay control passed |
| Training review RED | 86 | 77 | 9 | 0 | Confirmed receipt omission/rewrite failures; original75 retained |
| Training targeted GREEN | 89 | 89 | 0 | 0 | Receipt86 plus ordinary bus3; nonexistent migration filter adds no coverage |
| Work scaffold RED | 110 | 0 | 110 | 0 | Compiling detached empty APIs |
| Work review RED | 145 | 142 | 3 | 0 | Prepare changed range/tool/target revision before publication |
| Work targeted GREEN | 145 | 145 | 0 | 0 | Kernel116 plus29 retained work/locality/restore cases |
| Generation scaffold RED | 154 | 4 | 150 | 0 | Compiling empty API; native publication faults not yet reached |
| Generation review RED | 226 | 208 | 18 | 0 | Six reviewed guard classes; original154 and two real-document controls passed |
| First corrected generation attempt | 226 | 225 | 1 | 0 | Original unresolved-parent ambiguity regressed; failure preserved and source corrected |
| Generation publication review RED | 242 | 230 | 12 | 0 | Terminal transition plus ordinary postpointer exception bypassed final return gate |
| Generation exact-repeat review RED | 246 | 243 | 3 | 0 | Terminal transition during stored closure reread bypassed repeat return gate |
| Generation targeted GREEN before compact paths | 246 | 246 | 0 | 0 | Store139/terminal55 plus retained recovery52 |
| First full Core | 1,578 | 1,578 | 0 | 0 | Source before the platform path correction |
| First full Unity EditMode | 1,892 | 1,864 | 11 | 17 | Native initial commits failed; vendor skips retained |
| Compact path contract RED | 20 | 2 | 18 | 0 | Path/framing/map/pre-read/budget/ambiguity assertions; two passes on old hierarchy are not flat-layout proof |
| Corrected generation targeted GREEN | 266 | 266 | 0 | 0 | Store157/terminal57 plus retained recovery52; original194 assertions preserved |
| Final full Core | 1,598 | 1,598 | 0 | 0 | All416 new cases discovered on frozen corrected source |
| Final full Unity EditMode | 1,912 | 1,895 | 0 | 17 | All416 new cases and13 native records reached; existing external Critter skips |
| Final selected Unity PlayMode | 14 | 14 | 0 | 0 | Existing work, lifecycle, frontend and Continue compatibility |

Final named Unity discovery is receipt86, kernel116, generation store157 and terminal57. All nine product C# files match the final Core/Edit/Play source inventories; all eight new metadata GUIDs are valid and unique. The coordinator source is `40941caa71aa105381b9c1a362ca70064a4d7a4a26c1145d49df1aee57f34524`. [Training review](../../artifacts/component-integration-2026-10-02/training-review.md), [kernel review](../../artifacts/component-integration-2026-10-02/kernel-review.md), [generation review](../../artifacts/component-integration-2026-10-02/generation-review.md) and [coherent final review](../../artifacts/component-integration-2026-10-02/final-integration-review.md) approve the actual bounded diff with no scoped P1/P2 remaining.

Verified local product commits, created serially with exact path staging and unchanged working-byte checks:

- Training: `04b7d2ed296321d20004f91b53883e421a586964` (three paths).
- Detached work: `558333afdcfaac9448b238f050e086d8f810ae8e` (eight paths).
- Supplied generation storage: `97440c72df41ead506fbe3a6235befec1fc2abdd` (six paths).

The [commit record](../../artifacts/component-integration-2026-10-02/product-commits.json) verifies exact committed paths and retained working hashes. A coordinator-only helper naming collision was stopped before any Git mutation; HEAD/index were checked unchanged before the corrected script ran. This was infrastructure, not a test failure or a source change. This note and its verification JSON are a separate evidence commit; no author planning/audit files were staged.

The [preservation check](../../artifacts/component-integration-2026-10-02/final-preservation-check.json) verifies matching final source, unchanged nonproduct source/data, unchanged frozen contracts, empty index before commits and exact baseline settings bytes after reversing only two generated deltas. Task Editors exited; unrelated Unity MCP PID29812 remained. No source/cache diagnostic fixture was added. No command targeted the original checkout or profile saves; this is not a separate full-directory hash audit. No remote operation occurred.

The generation child fixture originally reused parent text. After retaining scaffold RED, its valid run/world state and layout/slice bytes were made distinct while retaining all154 assertions; immutable kit bytes may match. Three extra kernel controls were added after implementation and are not labeled preimplementation RED. Publication RED initially had an inventory-only review package due to an incorrect packaging argument; the incomplete artifact is retained, and its complete replacement embeds SHA256-verified executed preimages. No product reset or RED rerun occurred. Metadata is outside the .NET C# inventory and is separately validated.

Final Unity generation execution reaches13 fresh retained Windows native records: four slot-family exact-byte restarts, four prepublication lock/truncation failures recovering the complete old bundle, one throw-after-pointer recovering the complete child bundle, two durable frozen/terminal tombstone refusals after pointer/index loss, and two oversized-root refusals before writes. The original11 operations run at147-character roots; the two budget refusals run at191-character roots. These use task-owned disposable directories. Supplied one-room layout/slice diagnostics are synthetic; actual nonempty kit and coherent-ship layout/slice controls also pass. No natural live capture, earned journey or universal power-loss guarantee follows. [Flush documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0), [replacement documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0) and [Windows replacement semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew) were checked; actual execution uses .NET8 and Unity6000.6.0f1.

## Compatibility and remaining gates

Production run6/world4 schemas, ordinary save/load/NewRun/terminal paths and supported new-run settings are unchanged. Work summaries require typed longs and have no general JSON codec. The new generation envelope uses canonical decimal-string internal Int64 metadata while retaining supplied document text exactly. Its compact paths replace only an unpublished diagnostic convention; old attempt roots are retained and no legacy migration is introduced. Conservatively unsupported profiles/references and Windows roots exceeding the reserved path budget are refused without rewriting bytes. Non-Windows/virtual mappings make no Windows portability claim. Separate coordinator instances require external serialization; immutable staging/generations are retained pending an explicit janitor contract.

Prepared work can defer reconciliation if an uncertain prior publication changed target eligibility. The future real port must return the matching retained receipt before gating an unapplied candidate and independently validate authoritative live context. Fake ports do not establish real owner atomicity. Future F03 must stage actual XP/progression, receipts, stacks, jobs, debits, registry, holders and machinery through one publisher.

Remaining activation gates are the expanded single F03 owner and real selected-instance remove/mount UI preserving ID/condition/mass/holder; independent machinery health and explicit unknown condition; full live F05 assembler/all slots/Title/Continue selected handle, terminal lifecycle/reclaim and reference leases; F06 legacy preservation/preflight and usable Inspect/paid Overhaul before conversion; and normal source/tool acquisition and final placement qualification. Existing anonymous live shortcuts remain known gaps until that coherent integration. No survival/class/resource/time/offline/succession/colony/ecology policy changed.

Not run: full PlayMode suite, standalone player build, native OS input, live instance-aware workflows, legacy migration, earned all-class home/first-ship routes, natural long-term survival/performance, hardware powercut or cross-platform atomicity certification. Current catalog remains known-invalid and nongating:166 error rows/150 warning rows, including45 missing-definition and116 missing-source rows. The final canonical report still hashes to `f6b53c65f73abf91af7a7385efe0bf487fcd0fa52edc3a75b29900a7e3a0495c`; original evidence is unchanged. These rows are diagnostics, not unique-item or world-wide counts.
