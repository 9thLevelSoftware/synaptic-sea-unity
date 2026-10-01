# Persistent survival verification

Finishing the four introductory home objectives now checkpoints the ongoing life. It does not show victory/results, pay terminal meta rewards, stop survival or delete the run. Death still ends the life and supports results/New Run. Saved complete and partially complete objective markers restore their actual eligibility and HUD progress without replaying rewards.

This correction includes the immutable `constrained_expedition_v3` generator checkpoint. Later new size-1/size-2 contacts use varied spatial partitions, physical loops/branches and function-specific adjacency/furnishing. Existing saved v1/v2 visited layouts retain their identity. The starter hub and first-away remain the supported safe onboarding geometry. Rectangular single-deck hull envelopes, prototype props and unfinished art remain; this is not completion of the long-haul survival game.

## Normal play

Development build: **0.1.0+3b85cb0**, source commit `3b85cb02c049da38e58603697587dc52728333c4`, stamped **2026-10-01T04:31:16Z**, Unity 6000.6.0f1, Windows Mono, 489.8 MB, zero build errors.

```text
F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\persistent-survival-20261001\TheSynapticSea.exe
```

Build log: `builds/logs/persistent-survival-development-build.log`. The packaged `TheSynapticSea_Data/StreamingAssets/build_stamp.json` records kind `dev` and Git SHA `3b85cb0`. Production Core assembly SHA-256: `B9782490B817FB3BAAE61FF62A13935801740E3AD2DCC4883AC76B1A9C0A90AF`. The development executable was built successfully but not driven with native desktop input; the separately compiled Windows test player supplied the gameplay/render acceptance evidence on the same final source.

Use this distinct development executable. Preserve the old executable and current save. Close your old player normally before opening another player that accesses the same save; automated test players use isolated in-memory storage and do not access that save.

For a new life, choose the supported default Milestone A setup: seed 17, breach_field, standard, engineer. Walk, explore/search the starter supplies and maintenance cache, acquire the portable oxygen pump, and perform the real required repairs with the acquired tools/materials and earned repair training. Finish the home objectives if desired; the HUD should show **Explore, repair and survive — 4/4**, and ordinary saving/travel remain enabled.

Board the repaired lifeboat and use Tab near its bridge to access the scanner/travel controls. Complete the ordinary first-away visit and return. On the next trip select an eligible **unvisited** size-1/size-2 contact. In the seed-17 world, `-1:0:0` and `-1:-1:1` are the verified later choices (12 and 11 rooms in this version). Contacts already visited retain their older saved profile: choose another unvisited eligible contact. New Run and the first-away will not look like the new later expeditions.

Use F5/save and Continue to check the same life and visited layouts. Save/Continue after all home tasks must retain 4/4, searched containers, repairs, earned skills, equipment, defeated encounter state and ongoing survival. Returning home does not extract or end the life.

## Evidence boundary

The acceptance journeys drive the real player CharacterController and interaction/attack methods, use ordinary world supplies and skill/tool/material/travel checks, and retain physical path, standing, reach, line-of-sight and door assertions. Ship-owned bridge controls are resolved after bounded dock/portal reconciliation; copying their pre-LateUpdate anchor produced a stale-target failure during an earlier aggregate. No walking assertion was removed.

Windows captures are actual GPU camera and HUD panel output composited by the test capture helper, at 2048 x 1224. They are not generated imagery or native desktop keyboard/mouse proof. No automated foreground input was sent to the user's active session. The restored-onboarding capture is local at `builds/StandaloneWindows64/artifacts/ramp-review/persistent-home-onboarding-continue.png`.

## Remaining survival work

See [persistent-survival architecture and mobility audit](../design/persistent-survival.md). Durable welded home membership, traversable multi-vessel geometry, owned propulsion/power with aggregate load, exact connection-site persistence and long-horizon recovery/resource pacing still need implementation. Existing percentage-based propulsion cannot honestly establish a mass/thrust-based mobile home. Radiation recovery is an identified pacing concern; no rates, class balance or supplied resources changed here. Explicit internal legacy extraction remains compatible but is not a normal gameplay escape journey.

## Final verification — 2026-10-01

| Check | Result | Evidence |
| --- | --- | --- |
| Core aggregate | 759 passed, zero failures | `builds/logs/persistent-life-release-core.trx` |
| Edit Mode aggregate | 1,036 passed, 17 existing upstream skips, zero failures | `builds/logs/persistent-life-release-edit.xml` |
| GPU Play Mode aggregate | 61 passed, zero failures | `builds/logs/persistent-life-restored-play.xml` |
| Fresh Windows GPU player | Four passed, zero failures | `builds/logs/persistent-life-restored-windows-local.xml` |

The final Windows run finished at **2026-10-01T04:28:50Z**. The real full-HUD capture was inspected and shows **Explore, repair and survive — 4/4** after Continue. Prototype reactor holograms/tooltips and unfinished props remain visible; this capture does not establish final art quality. Windows test runtime assembly SHA-256: `30F9D542E2C9E7AD07239B05E89218E5BD09A9D55F3DC36E5AF70A0F3BCB549B`.

Both longer Windows journeys finish all home objectives before departure, physically fight a generated first-away encounter, loot the wreck, return and Continue, revisit the saved defeated encounter, then travel to a new v3 contact, save/Continue there and return/save/Continue again. The short journey verifies full restored HUD/marker eligibility, actual survival advancement and persistent loot. The fourth check verifies actual hostile damage, death/results and New Run.
