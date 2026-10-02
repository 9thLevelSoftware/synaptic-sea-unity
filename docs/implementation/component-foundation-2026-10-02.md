# Component foundation A and loot placement correction — 2026-10-02

The bounded, unused component foundation A and separate loot placement correction are implemented, independently reviewed, verified and committed locally. This extends the [earlier bounded foundation work](foundation-bounded-2026-10-02.md) and [station-tier correction](station-tier-2026-10-02.md); it does not complete the game or activate the component workflow.

| Local product commit | Scope |
| --- | --- |
| `63891b2a4e28d216eb5380e438b27fbeeb22562e` | 16 new registry, pure transfer, domain publication, test and metadata paths |
| `9b7d8be65e853345907a08f5c48e6e982482afb2` | Six paths for optional loot root offset and invariant loot focus geometry |

Baseline was `6ec9e5262f1997aef748f907ffee44801ac5f885`, on `codex/foundation-bounded-2026-10-02` in the existing isolated checkout. Scoped engineers implemented separate paths; fresh reviewers inspected the actual integrated files. The coordinator alone ran tests, Unity and Git. Concurrent author documents remained read-only and untracked. No remote publication, original-checkout/save manipulation, production content grant or balance change occurred.

Authority and baseline gaps are recorded in the [deep audit](../audit-deep/README.md), [CONTENT-01/02 and component pipeline evidence](../audit-deep/content/content-audit.md), [implementer handoff](../design/formal-build-2026-10-02/implementer-handoff.md), [contracts](../design/formal-build-2026-10-02/contracts.md) and separately authored [F03 amendment](../design/formal-build-2026-10-02/amendments/f03-operability.md). The final bounded API contract is preserved in [shared-contract.md](../../artifacts/component-foundation-2026-10-02/shared-contract.md), SHA256 `aeb35ecb95fb041fb95de515e358c2599877bdb6058dbdaa004d11428d5ad754`.

## Implemented behavior and compatibility

`ItemInstanceState` validates stable instance/definition/form/holder identities, explicit positive finite mass, known condition or explicit unknown/null, provenance and revisions. Snapshot replacement is complete and validated; returned values are defensive copies. Unsafe mutable dictionary keys and cyclic graphs are rejected before copying. No definitions, IDs, mass or condition are silently invented.

`DomainBundle` contains only the registry, holder descriptors/revisions, independently saved machinery health and completion receipts. The instance holder is the sole location authority; inventory/cargo/slot membership and availability are derived projections. Known condition caps effective machine health against saved health without changing either value or introducing an operational threshold. Unknown condition remains unavailable and cannot be newly installed into a slot.

Pure transfer/swap preparation checks exact ownership, all expected revisions, both installation directions and complete-candidate count/mass limits. It conserves identity, definition/form, mass, condition and provenance. Only moved locations and touched revisions advance; machinery and old receipts remain unchanged. The coordinator stages every included domain and receipt privately, then publishes one immutable bundle reference. Rejections and prepublication faults preserve the full old state. Committed replay cannot repeat publication or notification. Postpublication presentation failure cannot undo the committed effect, including when an exception's virtual Message getter throws. Higher-revision in-memory import must preserve all old receipts unchanged.

These new APIs use typed `long` Variant integers. The project's JSON parser represents numeric fields as `double`; parsed JSON is explicitly rejected by this unused typed boundary. No JSON/save conversion, schema migration, durable generation, live inventory/UI/work binding or parallel live store was added. A future adapter needs its own exact validation and reviewed contract. The included domain does not cover fungible stacks, jobs, material debits, XP, world/save publication or threaded callers.

Optional `position_offset` is exactly three finite numeric coordinates applied once to the existing ship-local loot interaction root after the current floor projection. Invalid present offsets, float32 narrowing overflow and resolved-position overflow skip only that row with an actionable ID/field error. Absent offsets take the original path and preserve descriptor output; approaches/content/slot metadata remain copied. No production descriptor currently uses this field.

LootContainer focus retains its geometry and sensor instead of scaling by 1.15. The existing gold E/Search label and HUD focus affordance remain; other interactable kinds retain their prior scaling. No shader, root compensation, collider policy or source activation was added. Future authored offsets must separately qualify occupancy and the protected repair/breach consumers that reuse away loot positions.

## Executed verification

[Machine-readable evidence](component-foundation-2026-10-02-verification.json) records all phases, exact failure names, counts, source/build fingerprints, product hashes, review identities and local raw artifact paths. Raw logs/TRX/XML and review packages are preserved under the ignored `artifacts/component-foundation-2026-10-02/` directory; their hashes are committed in the evidence record.

| Phase | Discovered | Passed | Failed | Skipped | Interpretation |
| --- | ---: | ---: | ---: | ---: | --- |
| component-red | — | — | — | — | Missing fixture brace; compile-only failure, no discovery |
| component-red-2 | 173 | 1 | 172 | 0 | Empty API stubs; one lookup control passed |
| component-review-red | 205 | 180 | 25 | 0 | Review regressions, including retained parsed-JSON replay failure |
| notification-red | 1 | 0 | 1 | 0 | Throwing Message getter misreported an already published effect |
| offset-core-red | 22 | 3 | 19 | 0 | Original offset omissions; compatibility controls passed |
| offset-ui-red | 5 | 2 | 3 | 0 | Original focus geometry failures; dispatch/nonloot controls passed |
| component-green | 245 | 245 | 0 | 0 | Component 223 plus offset Core 22 |
| component-edit-green | — | — | — | — | Bee response-file write failure before discovery/XML |
| component-edit-green-2 | 162 | 162 | 0 | 0 | Incomplete: all 88 domain cases omitted by three malformed GUIDs |
| component-edit-green-3 | 250 | 250 | 0 | 0 | Complete actual component 223 plus offset Core/UI 27 |
| offset-grounding-probe | 1 | 1 | 0 | 0 | Temporary synthetic empty-container physical witness |
| component-core-final | 1182 | 1182 | 0 | 0 | Full Core aggregate after temporary fixture removal |
| component-edit-final | 1496 | 1479 | 0 | 17 | Full Unity EditMode aggregate after removal |
| component-play-final | 14 | 14 | 0 | 0 | Selected existing work/lifecycle/front-end regressions |

Final discovery explicitly includes registry 90, transfer 45 and domain 88 cases, plus offset Core 22 and Unity UI 5. The 17 EditMode skips are existing CritterCrafter environment exclusions: 15 need a built critter library, one needs a connector fixture and one needs export environment paths. No component case was skipped.

Initial stub failures establish new contracts, not natural gameplay bugs. Fresh review reproduced mutable-key copy aliasing and strict integer validation failures before correction. The original parsed-JSON replay failure and complete original test remain preserved in `component-review-red-review-package.md`; the positive replay input was changed to a typed summary copy while retaining every replay/effect assertion, and an explicit parsed-JSON rejection case was added. Cyclic-graph controls were added after the guard; no preguard recursion/red claim is made. The independent-health 0.81 condition/0.2 health control was likewise added after implementation.

The 162-case Unity result was rejected as incomplete despite exit 0. Its log preserves the three invalid 33-character metadata GUIDs. Only those GUIDs were corrected to unique valid 32-hex values; all C# and assertions remained unchanged. The subsequent 250-case run and final aggregate contain all 88 previously omitted domain cases.

Final Core source inventory SHA256 is `5b232b17ed822311fee4bc7adc827bb178fed8e9b32066d92f4d837c6a5bdc90`; final Unity inventories share `882bc76e1bfa88d5a2b628312beb7b515e9eafecb2a1afc686f0a18714ae7255`. Final Core test assembly is `4b52ae9891379fc6fd26d2e6c6e62b56e42836289570a5e7cacd47621930b68d`; Unity Core is `99a23fabeaeb88526681a23054eae532b983d485e4391e1fa96373f9d4c78ac3`, and EditMode tests are `ced08c6b172ffab9a42a4525e0b94bc25bb07f1b24a637e59c5f1e5edd78f7c9`. Applicable scoped C# bytes match these executed inventories; metadata is separately hashed and validated.

## Physical witness and environment handling

The [offset review](../../artifacts/component-foundation-2026-10-02/offset-review.md) independently approves the frozen six-path correction. Its executed temporary fixture used ordinary default Title/New Run, then inserted a synthetic empty container. Actual maintenance floor cell `1|5|1` has support top 4.125 m. Offset `[-0.8,0.00500011444091797,0]` produced interaction root `[19.2,4.125,4]`; focused and unfocused renderer bottoms both remained at 4.125 m with identical nominal `[0.81,0.9,0.675]` dimensions. Only supporting-floor face contact was permitted; other visual, blocker and initial closed-door footprints were checked.

Normal deck/door/focus/Search dispatch and bounded NavMesh/capsule/floor/portal checks were exercised, with every deliberate fixture teleport retained in the log. Empty Search changed no inventory, XP, books or objective sequence. The [game-camera image](../../artifacts/component-foundation-2026-10-02/offset-grounding-probe/offset-grounding-61746e3e33914d7a8e63cee18bf5c91f/synthetic-empty-candidate-game-camera.png) supports geometry only; it does not capture the HUD or prove native input. The original marker-centered 0.55 m NavMesh sample failure remains. This witness does not approve final authored placement, earned acquisition or an all-class route. Source/meta were preserved outside Assets and removed before all final aggregates.

The first Unity GREEN attempt failed before discovery because Bee could not write response files. After its Editor exited, the coordinator verified task-local paths, no reparse points and file hashes, then non-destructively preserved only 144 derived `.rsp` files and let Unity rebuild that directory. No ACL changes, process killing or failure reclassification occurred. Exact root cause remains undetermined. The unrelated Unity MCP PID 29812 and earlier cache/audit witnesses were preserved.

After final Editor exit, only the two generated settings changes were reversed; both settings files exactly match their pre-run baseline hashes. No temporary diagnostic fixture or generated setting is committed. The separately reviewed 33-file author packet, manifest SHA256 `58b5d89a0352912b9bd598d80a7f5ca157e95678ca85780ec9e2cda2826413fc`, was copied with verified hashes into an ignored snapshot; its source documents were neither edited nor staged.

## Review closure and remaining gates

The [fresh final component review](../../artifacts/component-foundation-2026-10-02/final-review.md) approves all 16 final paths and the integrated evidence with no remaining P1/P2 finding. Final package SHA256 is `cc85385df9a76ffed190ca0e414b57723909d8af568461668343ee856dab85f3`. The separate offset reviewer also approved its actual final diff, regression results and synthetic physical witness. Neither reviewer launched tests or operated Git/Unity.

Ordinary catalog output remains deliberately invalid and nongating for game launch: 166 error rows and 150 warning rows, including 45 `missing_definition` and 116 `missing_source` rows. These are diagnostic rows, not unique items or world counts. Its SHA256 remains `f6b53c65f73abf91af7a7385efe0bf487fcd0fa52edc3a75b29900a7e3a0495c`, identical to the earlier retained catalog evidence. Missing normal wrench acquisition, 11 undefined carried component forms and the live legacy condition/health pipeline remain unresolved; these unused primitives do not repair that live chain.

Activation requires the reviewed F04A work kernel before later component work consumers, sole real publication/live holder adapters, actual selected-instance extract/carry/install/swap UI, explicit unknown inspection/paid overhaul and independent legacy preflight, full all-slot durable generation/binding/migration, and final authored kit/source occupancy qualification. This batch has no remaining implementation blocker within its admitted scope.

Not run: full PlayMode suite, standalone player build, native OS input, earned acquisition/all-class home or first-ship progression, live component UI/work integration, paid legacy overhaul, durable multi-file generation/migration fault matrices, and long-term survival/performance. No survival rates, class stats, resource quantities, supported new-run settings, succession, offline policy, topology fidelity or ecology were changed.
