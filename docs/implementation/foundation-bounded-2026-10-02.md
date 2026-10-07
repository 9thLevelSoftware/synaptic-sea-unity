# Bounded foundation implementation evidence — 2026-10-02

The three independently admitted corrections are implemented and verified. This note covers catalog diagnostics, existing generic work correctness, and single-file storage recovery. It does not certify F01–F08 or a finished game.

Baseline: `840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48`, with 27 local commits beyond audited published main `8dcc95c10ab5e08658546319f51f49b4abd256fc`. Work stays on local branch `codex/foundation-bounded-2026-10-02` in the existing isolated checkout. Three engineers owned separate source files; the coordinator alone ran tests, Unity and git. Each task received independent review and a correction/re-review wave. A fresh reviewer approved the actual entire final 21-file diff and retained failures/fixes, finding no remaining significant P1/P2 issue or scoped blocker. The [final review](../../artifacts/foundation-bounded/final-review.md) includes package hashes, final source matching and execution checks.

The [deep audit](../audit-deep/README.md), especially integrated findings 1, 4 and 6, supplies the baseline. Exact work/save diagnostic states and fingerprints are retained in [v7 model results](../audit-deep/runtime/results-v7/model-results.json) and [experiment manifest](../audit-deep/runtime/experiment-manifest-v7.json). The [handoff](../design/formal-build-2026-10-02/implementer-handoff.md) and [persistence proposal](../design/formal-build-2026-10-02/persistence.md) distinguish this admitted batch from pending transactions and migrations. Those planning/audit files belong to the concurrent documentation author and were neither edited nor staged here.

## Implemented behavior

**A / F01 narrow:** `CatalogSourceValidator` reads real merged ItemDefs without modifying their merge semantics, preserves origin paths and typed consumer references, and normalizes actual session registrations. It reports missing item/tool definitions, registered sources, AND-input/tool closure, station/tier/skill/book gates, malformed resources and quantities, and invalid dispositions. Diagnostic/deferred producers cannot satisfy production consumers. Deconstruction follows the actual salvage caller while retaining its authored origin. The on-demand session report does not gate ordinary launch. Known missing content remains actionable: 115 items, 62 recipes, 11 component definitions, 22 skills, 12 books, 351 references, 107 current-session edges and 24 potential item IDs. Its 45 missing-definition and 116 missing-source rows are diagnostic rows, not unique-item or global-world counts.

**B / F04 narrow:** generic work continuously checks its original ship/context, exact target and existing interaction anchor/range before progress, stamina, noise or completion effects. Moving 1,414m away interrupts the actual command-started job. Release pauses held work; tap behavior remains covered. Restored jobs clear physical input, retain target/progress, show `resume_required`, and require explicit validated input to resume. Existing strict repair/seal and special home guards remain. The reviewer-discovered legitimate room-center weld fallback retains its original anchor policy. Canceling a pending restored job clears its resume feedback.

**C / F05 narrow:** filesystem publication flushes same-directory staged UTF-8 bytes, then uses supported replacement with an old-file backup or a no-overwrite move. It removes the application-controlled delete-before-move gap. A valid legacy temp can recover only with absent canonical destination, current complete shape/identity/references and consistent existing witnesses. Frozen/dead/corrupt, incompatible, ambiguous or malformed candidates remain rejected and retained. Slot listing recovers before corrupt-marking; deletion/freeze/reference scans include staging so recovery cannot resurrect terminal runs or lose required generated layouts. World recovery reuses existing propulsion-owner and dock-graph validation through a pure overload; live session graph behavior is unchanged.

## Verification

Raw logs, XML/TRX, source inventories, build hashes and full failure assertions remain under `artifacts/foundation-bounded/`. [Verification summary](foundation-bounded-2026-10-02-verification.json) records the final source identities and exact commands. Every phase used a distinct output path. Initial infrastructure permission and Unity compilation attempts are reported separately from executed test failures.

| Phase | Discovered | Passed | Failed | Skipped | Interpretation |
| --- | ---: | ---: | ---: | ---: | --- |
| Baseline Core | 796 | 796 | 0 | 0 | Unchanged product baseline |
| Catalog initial red | 13 | 0 | 13 | 0 | Compile-ready empty implementation |
| Catalog session / strictness / gate reds | 2 / 3 / 2 | 0 | 2 / 3 / 2 | 0 | Missing session adapters and diagnostic checks |
| Catalog fresh-review red | 29 | 1 | 28 | 0 | Four reproduced diagnostic gaps; valid empty-tool control passes |
| Catalog final covering green | 53 | 53 | 0 | 0 | 49 new cases plus four existing catalog cases |
| Work initial red | 16 | 3 | 13 | 0 | Actual command locality and released restore regressions |
| Work first green attempt | 70 | 66 | 4 | 0 | Exposed sparse pristine-module restore dependency; subsequently corrected |
| Work weld / cancel review reds | 1 / 1 | 0 | 1 / 1 | 0 | Preserved fallback-anchor and stale-feedback discriminators |
| Work final covering green | 94 | 94 | 0 | 0 | 22 new cases plus 72 existing cases |
| Storage initial red | 31 | 22 | 9 | 0 | Real Windows lock/recovery failures and negative controls |
| Storage expanded attempt | 47 | 45 | 2 | 0 | Future mobile-state rejection defect; one fixture comparison corrected against canonical JSON control |
| Storage menu / world-guard review reds | 3 / 2 | 0 | 3 / 2 | 0 | Actual listing poison and application-validator mismatch |
| Storage final covering green | 82 | 82 | 0 | 0 | 52 new cases plus 30 existing cases |
| First integrated Core | 919 | 919 | 0 | 0 | All 123 new Core cases discovered |
| First Unity Edit attempt | — | — | — | — | Exit 1 before discovery/XML: bundled NUnit lacks `NonParallelizable`; test-only conditional guard added |
| Final aggregate Core | 919 | 919 | 0 | 0 | Exact Core/Procgen/Edit source after the conditional guard |
| Full Unity EditMode | 1221 | 1204 | 0 | 17 | All 123 new Core cases and both new UI cases passed; vendor fixture skips |
| First selected GPU PlayMode | 14 | 13 | 1 | 0 | Restored-tap fixture bypassed actual UI preference persistence; released progress assertion retained |
| Real UI input-mode discriminator red | 1 | 0 | 1 | 0 | Confirmed restore reapplies persistent hold mode when only in-memory toggle is changed |
| Final selected GPU PlayMode | 14 | 14 | 0 | 0 | Both new work cases plus 12 Title/launch/Continue/death/extraction/failure cases |

The toggle fixture uses `MenuCoordinator.ApplySettingsSummary`, the existing cached/persistent preference path; it retains all old progress assertions and adds mode discriminators. No product preference policy changed. Core/Procgen/Edit/UI source stayed byte-identical during this Play-only correction, so their completed aggregate checks remain applicable; final Play compilation and execution cover the corrected fixture. The 17 EditMode skips are 15 missing-built-Critter-library cases, one missing connector fixture, and one export probe without its FBX/catalog environment paths. Full PlayMode, a standalone player build, native input, earned class journeys, killed-process/power-cut recovery and cross-file generation fault injection are not run in this batch. Five supplementary work cases were initially added after implementation and are labeled as such in the work report; the pending-cancel case subsequently received an observed red. No failure assertion was removed to obtain green results.

Unity ran serially with graphics through editor `6000.6.0f1`. Its package initialization changed only two isolated ProjectSettings values; their exact diff was retained and the confirmed generated changes were restored after the last process exited. The transient platform disconnect did not block local execution or interrupt an observed test/write. No uncertain command was blindly rerun. Unity's [command-line test filter contract](https://docs.unity3d.com/6000.3/Documentation/Manual/test-framework/reference-command-line.html) informs the preserved selected-test command.

## Compatibility, remaining findings and local commits

Run/world versions remain `gate2-current-run-6` / `world-4`; no migration or new persisted fields were introduced. Existing canonical older-save migrations remain. Older/identity-free temporary payloads are conservatively retained and rejected. Existing standalone manual-slot partial restore semantics remain; ambiguous legacy work ownership requires restart instead of inventing a vessel binding. Already completed jobs cannot replay effects through restore.

Staged data flush and local Windows replacement/lock tests do not prove directory metadata or hardware power-loss durability. See the [File.Replace contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace), [Flush(bool)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush) and [Windows replacement failure behavior](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew). Complete-generation publication, backup recovery after process death, new slot binding, index/cloud repair and schema migrations still need reviewed contracts. The catalog graph is potential closure: finite spending, remaining stock, loot-context suppression beyond current content, power/access/traversal, training order and earned solo routes remain unproved.

Missing definitions/sources/books and other audit findings remain: original-home services, structural atmosphere semantics, visitation catch-up policy, offered launch/caption feedback and production creature registration. No survival rates, class stats, quantities, supported launch settings, death succession, offline policy, topology fidelity, ecology, saves or user assets were changed. Provisioned scene/model state proves an invariant; it is not natural gameplay or game completion.

Focused product commits, made only after aggregate verification and final review:

| Scope | Local commit |
| --- | --- |
| A: production catalog diagnostics | `c68c3fe80c322c213b076ffdd1f588596097d1cf` |
| B: work locality and explicit restore input | `e062f00386c4ce789dc2a481a5a33568902ff85a` |
| C: destination preservation and validated legacy-temp recovery | `52027d3e8cf31a699c4a4f2b94ea0b7cb571462c` |

The separately committed evidence note/summary records exact command/discovery counts, source/build fingerprints and preserved failure paths. Raw artifacts remain local and ignored. No push, PR, merge, deployment, hard reset, blanket staging or original-checkout/save manipulation occurred. No environment blocker remains; the broader content, transaction, generation-binding and migration amendments are dependencies for subsequent work.
