# Interior camera, zoom and foreground reveal

The camera now uses a fixed diagonal orthographic view at approximately 30 degrees downward, with a default orthographic half-height of 7 metres (14 metres of vertical view). The former view used half-height 11 and an approximately 38.5-degree elevation. Room dimensions, custom module placement, colliders and navigation are unchanged.

This is Project Zomboid-inspired project tuning, not a claim about PZ's engine angles or constants. The two floor axes project to 2:1 screen diagonals. PZ's [official zoom API](https://www.projectzomboid.com/modding/zombie/core/textures/MultiTextureFBO2.html) exposes current/target zoom and bounded levels; its [wall-cutaway development notes](https://projectzomboid.com/blog/news/2018/12/nightzedding/) emphasize keeping wall context while revealing the player.

## Controls and behavior

- Scroll up to zoom in; scroll down to zoom out. Half-height levels are 4, 5, 6, 7, 8.5, 10 and 11 metres, with a 0.16-second smoothing parameter. Orientation remains fixed and the player stays centered.
- Menus, inventory and modal gameplay gates suppress zoom. Scrolling over a UI scroll view also suppresses camera zoom.
- The eligible focused object gets an anchored `E · <action>` label using the same focus selected for interaction dispatch.
- Parallel orthographic rays sample the player's feet, torso, head and shoulders, plus the focused interaction anchor. All blocking structural wall/portal renderers and relevant upper-deck surfaces are considered. The player's supporting floor stays visible; the established interior ceiling cutaway remains in effect.
- Simple retained URP materials fade to 8% opacity. Complex or unsupported purchased/custom shaders use reversible renderer hiding instead of incompatible blending. A thin noncolliding floor footprint preserves the obstruction boundary. This is a fade/hide implementation, not PZ's curved ankle-height mesh cutaway.
- A depth-tested cyan silhouette applies only to the controlled player behind opaque obstructions. Enemy renderer visibility remains constrained by physical player-to-enemy line of sight; hiding a wall does not expose an enemy behind it.
- Changes restore after movement, focus/zoom changes, teleport/deck changes, disable/destroy, scene teardown and inactive pooled renderers. Shared materials are not modified; temporary fade clones and footprints are destroyed on restoration.

The live docking union replaces some module-owned colliders, so obstruction detection also checks actual structural renderer bounds. This fixes a real discrepancy found during room rendering rather than assuming every visual wall still owns its collision box.

## Verification on 2026-09-30

| Evidence | Result |
| --- | --- |
| Full Unity Edit Mode aggregate | 1,001 passed, 0 failed, 17 existing upstream fixture skips; `builds/logs/camera-final-edit.xml` |
| Full GPU Editor Play Mode aggregate | 54 passed, 0 failed/skipped; `builds/logs/camera-final-play.xml` |
| Independently built GPU Windows test player | 5 passed, 0 failed/skipped; `builds/logs/camera-final-windows-local.xml` |
| Windows tests exercised | Title/New Run real scene bootstrap, wheel/focus behavior, room close/far/upper-deck rendering, F5/F9 save/load, retained URP and silhouette shader support |
| Existing Core suite | Previous 732-pass result; unchanged Core code, not rerun for this presentation slice |

Edit tests cover multiple blockers, off-axis focused targets, restoration of materials/property blocks, unsupported materials, pooling/disable, zoom, upper floors/teleports, physical enemy visibility and composite docking geometry. Play tests preserve structural transforms and collider enable states and assert the player grows appropriately on screen when zooming in. The existing full Play aggregate includes natural first-away/combat/return/save/continue and lifecycle journeys.

The first quiet Windows launch lost Editor callbacks after disabling automatic foreground launch. The supported NUnit callback now writes independent player results; the Editor's no-callback exit is not counted as a passing test run. The actual player result reports five passes, and that player exited cleanly. No native OS keys were sent.

Actual Windows GPU render/readback images, all 2048 x 1224:

- `builds/StandaloneWindows64/artifacts/screenshots/camera-before.png`
- `builds/StandaloneWindows64/artifacts/screenshots/camera-after.png`
- `builds/StandaloneWindows64/artifacts/screenshots/camera-close.png`
- `builds/StandaloneWindows64/artifacts/screenshots/camera-far.png`
- `builds/StandaloneWindows64/artifacts/screenshots/camera-upper-deck.png`

These are camera-only game renders from the test player, not generated images or native desktop captures. They show larger indoor objects and foreground reveal while retaining surrounding walls. The existing dark atmosphere and placeholder object art remain; no lighting redesign or performance benchmark is claimed. UI label visibility/anchor is asserted, but these captures do not include the HUD.

## Opening and playing

Open `F:\tmp\synaptic-sea-playable\SynapticSea` in Unity 6000.6.0f1, open `Assets/Scenes/Title.unity`, enter Play Mode, choose New Run and the supported Milestone A defaults, then Start Run. Movement and E interaction use the existing controls; wheel zoom is available during gameplay. F5 saves and F9 loads the current run.

The distinct camera development executable is `F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\camera-zoom-20260930\TheSynapticSea.exe`. Close the older game yourself when convenient before opening this version. The shader-fix executable and original repositories/assets/saves are preserved. Build result and exact stamp are recorded in the main audit after completion.

The owner already confirmed loading, movement and interactions in the previous shader-fix build. Native agent-driven input and owner acceptance of this new camera build remain pending desktop availability. Colony scope, class progression decisions and owner creature approvals remain unchanged and pending.
