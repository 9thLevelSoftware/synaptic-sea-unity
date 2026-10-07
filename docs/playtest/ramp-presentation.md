# Landing and cutaway presentation repair

The reported starter-hub screenshot shows the character below an upper landing. Reproduction with the actual Title/New Run scene found the player's feet at approximately 0.14 m on the lower deck, with the upper platform around 4.1 m. The controller was physically standing on the lower floor. It was not penetrating the upper platform.

The camera reveal hid the upper floor but generated a floor-sized `CameraWallFootprint` at the upper height, recreating the obstruction. Each wall trim also received a separate footprint at its own renderer minimum height, leaving horizontal floating strips. Imported `_convcolonly` meshes rendered collision helper surfaces alongside the purchased floor's visible surface.

The repair excludes floor/ceiling footprints and wall bases on other decks. Revealed walls and doors retain one subtle boundary at the logical assembly's base. Collision helper renderers are disabled; their collision and original source content are retained. Camera fading still restores original shared materials, never changes collision or AI line of sight, and does not expose hidden enemies.

Presentation trigger cubes and duplicate landmark/blockage cubes no longer render as furniture. Their triggers/colliders and the existing semantic props remain. Breach, arc and blocked-route overlays become thin floor warnings rather than tall translucent volumes; the original blocker collision and hazard behavior remain. Ordinary labels respect physical walls and deck height, while the eligible focused interaction prompt remains readable. Layouts without authored lighting use a brighter ambient/reflected interior fallback so metal floors can be distinguished from empty space; authored biome atmosphere and the legacy calibrated comparison environment are preserved.

## Verification

`RampLandingPresentationAndBothDeckTransfersPreservePhysicalStanding` starts through Title/New Run, walks to the landing with the real controller, transfers upward, saves/Continues at the upper landing, walks back and transfers downward, then saves/Continues at the lower landing. Inventory, skills and player position are not forced to bypass the route.

The independent Windows GPU test player captures the actual game camera and HUD panel into render textures at 2048 x 1224, then composites those rendered pixels. The capture asserts nonempty world and UI output. These are rendered test captures, not native desktop input evidence. Batch screen screenshots were black and were rejected as visual evidence.

Local captures are under `builds/StandaloneWindows64/artifacts/ramp-review/`: `ramp-lower-7.png` (default zoom), `ramp-lower-4.png` (close), `ramp-lower-11.png` (far), `ramp-upper-loaded.png` and `ramp-lower-loaded.png`. They have not been uploaded to Library. They show the unchanged starter layout and camera orientation, not the paused next expedition topology.

This is a targeted presentation repair. The environment still uses placeholder semantic props, and it does not claim finished art direction or eliminate every possible material/module issue. The owner's active game, saved world and original purchased/generated assets were not modified by test fixtures.

Final verification: **741 Core passes**, **1,017 Edit Mode passes with 17 unchanged existing skips**, **56 GPU Editor Play Mode passes**, and **1 targeted independent Windows GPU journey pass**, all zero failures. Logs: `ramp-final-core.trx`, `ramp-final-edit-v2.xml`, `ramp-final-play.xml`, `ramp-final-windows-local.xml` under `builds/logs`. The final Windows journey captures default/close/far lower-deck views and both saved landing states on the final runtime code. No native desktop keys were sent. No fixture altered the owner's save.
