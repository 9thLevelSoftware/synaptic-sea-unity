# GEN-01 bounded followup: normal decision commands

Revision: companion `00735856e4d06e5823608e0ab77832a992baf098`. Read-only command/caller inspection; no real approve/reject invocation, source status write, build, or package export performed.

Conclusion: normal decision commands do **not** automatically invalidate or refresh the built catalog. Explicit `library build` after the decision refreshes its status fields. The finding should be phrased as a release reconciliation gap when packaging without that explicit rebuild, not as bypassing the receipt gate or evidence of a currently revoked local approval.

Trace:

- `critter skeleton reject --owner ID`: `src/critter_crafter/skeletons/commands.py:327` routes at331 to `_set_status_owner:286`; it assigns source status/review at305-306 and `_write_json` at307. `_write_json:12-14` only makes parent directory and writes the selected authoring JSON. The command then echoes332. No built catalog, state, manifest or compiled asset invalidation/rebuild occurs.
- Without `--owner`, `skeleton_reject:331` calls `_set_status:263`: first checks `_built_catalog:265` and current review receipt at274-276, then updates source status at281 and writes at282. Again no compiled invalidation. The current crawler's unflagged rejection would fail its stale receipt before writing; this does not apply to the intentional owner decision route.
- `skeleton approve:315-320` uses the same setter paths. `cli.py:208-218` directly registers command groups; no result callback or close hook rebuilds/invalidate library state.
- `critter part reject ID`: `parts/commands.py:574-577` calls `_set_status:528`; `mark_status:515-524` records rejected and removes approved_pipeline; `_write_json:535` writes only the authoring record. Part approvals:543-569 use the same source writer and explicitly print instructions to rebuild at553/568. They do not invoke it automatically.
- Subsequent `library pack:641`: `_built_catalog:643,676-731` validates current source/artifact identity while excluding statuses (`_part_source_state:151-157`, skeleton `review.py:28-35`). It returns old built catalog731. Pack writes existing files648-650; both full and slim include the old catalog (`pack_files:625-635`). No current decision overlay is applied.
- Explicit `library build:472` calls `_catalog:476`, so current decisions enter the new compiled catalog. It publishes updated catalog at571 and619. Following this step avoids the stale decision issue; no audit build was run.

Evidence qualification: the earlier in-memory probe changed current compiled crawler status to rejected, called the real `_built_catalog(require_assemblies=True)`, and received PASS with old built approved. This proves decision-delta tolerance in the pack loader. The normal-command static trace establishes that normal source writes leave the same mismatch possible. It is **not** a persistent end-to-end approve/reject/ZIP experiment; none was authorized or needed for this read-only lane. Actual source and built crawler remain approved, matching the owner's 2026-09-30 decision.

Recommended wording: **After a normal approval/rejection command, packing an existing library without explicitly rebuilding can export its previous approval decisions.** Reconcile decision fields at pack time, or refuse decision mismatch with an actionable rebuild instruction, while keeping expensive unchanged geometry reusable. Add approve/reject→pack transition coverage. Severity remains medium conditional on a status transition followed by pack without rebuild.
