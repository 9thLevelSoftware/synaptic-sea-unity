# Critter runtime adapter and bounded asset review

The game now embeds Critter Crafter 0.2.0 from companion commit
`01230d84c895c13fdb744df98d5b39724c35ba44`; provenance is in the package's
`UPSTREAM.md`. Unity resolves its Animation Rigging dependency. No owner art,
approval record or purchased pack is included in the embedded source package.

## Live behavior and save contract

`RunSessionHost` supplies a `ThreatCreatureFactory` to the live threat view.
It uses an explicitly assigned `CritterLibrary`, or loads a Resources asset named
`CritterProductionLibrary`. Matching organic pools are `biomatter_swarm`,
`puppet_corpse` and `stalker`. `drone_swarm` remains mechanical. Mimics and hull
tendrils retain their game visuals because this catalog has no matching pool.

The adapter generates once, validates through the companion production validator,
checks every skeleton/part/connector model and assembles through its existing
`ICreatureVisualFactory` interface. Review recipes and draft production assets
are refused. Imported visual colliders are disabled; existing game hitbox and
navigation authority remain. Generated geometry lives under the direct `Mesh`
child so attack/hit/death feedback continues to work. The primitive factory keeps
its `Threat_<archetype>` root convention; the live view retains its existing
`ThreatPlaceholder_<instance>` names, avoiding changes to downstream consumers.

Successful creatures use their published per-build `move_speed_mps` for the AI
base movement speed, retaining existing AI state multipliers. The animation driver
observes actual game-owned motion, turns only the visual, disables root motion and
receives telegraph/attack/hit/stun/death. Placeholder speeds remain unchanged.

An optional `creature_visual` dictionary travels through the existing threat
summary/save paths. It contains exact recipe JSON, pool, library ID/version and a
deterministic instance-derived seed stored as a decimal **string**, avoiding the
game JSON parser's loss of integer precision. Saved recipes are authoritative.
Malformed, missing, incompatible, unapproved or missing-model recipes fall back
without regeneration. A save created without a usable library retains its failed
identity; start a new run after installing an approved library to generate new
visuals. Deliberate saved-creature migration is separate future work.

Fallbacks retain normal gameplay and expose a local `ThreatCreatureVisualStatus`
component with the reason; there is no repeated per-frame generation/log spam.
The adapter is functional with a synthetic approved geometry fixture. It has not
rendered an owner production creature because no production skeleton is approved.

## Minimum owner review to enable one production archetype

Actual local review output was found in `D:\critter-creator`, beyond the initial
audit clone. The minimum bounded candidate is
**`crawler_alien_tripod_balanced_v3`**, currently **draft**, serving both crawler
organic pools (`biomatter_swarm` and `stalker`). Its required branches are `core`,
`leg_0`, `leg_1`, `leg_2`; `crown` is optional. Compatible existing reference parts
include `reference_crawler_alien_tripod_balanced_v3_core_v1` and
`reference_crawler_alien_tripod_balanced_v3_leg_0_v1` (symmetric, usable for all
three legs). Reference parts are expressly permitted by the companion validator;
they are not production approvals. The optional crown can use compatible reference
parts or remain unfilled according to the recipe. No real Meshy part approval is
required to review this initial reference assembly.

View the actual local interactive review, with bones/mannequin/assembled modes
and all clips, by serving:

```powershell
F:\Tools\Python312\python.exe -m http.server 8765 --bind 127.0.0.1 --directory F:\tmp\critter-owner-review-current
```

Open `http://127.0.0.1:8765/?skeleton=crawler_alien_tripod_balanced_v3`.
An actual Unity turn/walk frame is also available at
`D:\critter-creator\work\review\turnfix-b\crawler_alien_tripod_balanced_v3_walk_turns\frame_0012.png`.
It is an existing review capture, not a new game production rendering or approval.
The accompanying metrics report 0 penetration, 2 minimum planted supports and
about 0.105 m maximum planted slip at the captured walk speed; foot slip/turns and
body silhouette merit visual review. The source remains draft. The companion's
freshness gate must verify the current receipt before any owner approval; stale
reviews require rebuilding/reviewing, never bypassing the gate.

After the owner approves the exact candidate in the companion workflow, rebuild
and pack the current library there. In this game, use **Tools > Critter Crafter >
Import Library Zip**, then assign the resulting library to the host or create its
Resources registration as `CritterProductionLibrary`. Preserve the imported
library and textures locally unless their publication is separately authorized.
Run a new game and verify the live status reports a validated production recipe.
Save/continue must restore the identical recipe and seed.

For textured production legs, the separate draft candidate is
`meshy_insect_leg_a_v1`; its owner review sheet lives at
`D:\critter-creator\work\review\parts\meshy_insect_leg_a_v1\sheet.png`.
This is optional and does not approve the other three real parts, additional
skeleton families or per-creature speed aesthetics.

## Verification

Regression coverage includes missing library, mechanical drones, an empty draft
pool, malformed/missing saved recipe, missing part model, rejected saved approvals,
identity mismatch, full threat-runtime JSON restoration, retained movement
multipliers, published creature speed and synthetic skinned-mesh assembly.
Final suite/build counts are recorded in `playability-audit.md` after the runs.
Broader colony resource/defense work is proposed separately in
`colony-defense-proposal.md`; it is not implemented in this adapter slice.

## Current owner-review readiness (2026-09-30)

The old receipt failed the normal gate with `CC_REVIEW_STALE`. The established
`skeleton review crawler_alien_tripod_balanced_v3 --out F:\tmp\critter-owner-review-current`
command regenerated the review: **8 clips passed, 0 diagnostics**. The normal
approval gate then verified the new receipt against the current content fingerprint
`592aea7536a285aea8982d8063a41d95e39641ffaeeed2af58a11d5f2d36dad1`.
The review contains 17 outputs, bones/mannequin/assembled modes and four camera
views. Receipt freshness is verified; owner visual approval remains pending.
No skeleton or part status was changed. The older Unity frame above is illustrative;
use the refreshed interactive bundle for the current approval decision.
