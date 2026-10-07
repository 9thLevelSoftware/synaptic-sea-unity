# Windows New Run shader regression

The reported player failure was `RuntimeVisualCatalog: shader 'Universal Render
Pipeline/Unlit' not found` while New Run built objective volumes. A title-only
smoke test does not exercise runtime-generated ship materials.

## Corrected development build identity

Absolute executable:
`F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\shader-fix-20260930\TheSynapticSea.exe`

Build stamp: `2026-09-30T15:37:04Z`, dev, Mono, Unity `6000.6.0f1`, version
`0.1.0`, 488.3 MB, zero build errors. The stamp has an empty Git SHA; source
identity is the local shader-fix commit and runtime binary hash recorded in the
audit. The old `builds/StandaloneWindows64/dev` output is preserved and is not
the corrected build. Do not copy just the launcher EXE: its neighboring Data,
UnityPlayer, graphics and Mono files are required.

## Automated regression

Run the regular Edit Mode suite. `RuntimeVisualMaterialTests` checks the
Resources library's URP Lit/Unlit dependencies, all six feature combinations,
clones/cache behavior, and rejection of incomplete or mismatched templates.
The build preprocessor applies template checks to manual and batch builds.

Run the player regression with GPU rendering enabled (do not use `-nographics`):

```powershell
& F:\Unity\6000.6.0f1\Editor\Unity.exe -batchmode `
  -projectPath F:\tmp\synaptic-sea-playable\SynapticSea `
  -runTests -testPlatform StandaloneWindows64 `
  -testFilter 'RetainedRuntimeVariantsAreSupportedAndUnlitAlphaRendersInThePlayer;TitleNewRunBootsPlayableAndQuitReturnsToTitle' `
  -playerHeartbeatTimeout 120 `
  -testResults F:\tmp\synaptic-sea-playable\builds\logs\shader-player-regression.xml `
  -logFile F:\tmp\synaptic-sea-playable\builds\logs\shader-player-regression.log
```

This is a separately built Windows test player. Both tests passed. The shader
test verifies supported URP shaders/keywords and reads actual green opaque and
alpha Unlit pixels, including blending. The bootstrap test creates the real
Playable scene through Title/New Run UI command dispatch. These tests do not
establish native OS keyboard/mouse interaction with the development executable.

## Pending native acceptance

Wait until the desktop is available; the owner is using VMware Horizon and
Windows currently keeps it in the foreground. Never send game keys to another
foreground process. With the corrected executable's **The Synaptic Sea**
window foregrounded, use native Title > New Run > Start with the defaults
`breach_field`, `standard`, seed `17`. Verify the setup overlay closes, world
geometry and markers render, WASD moves the character, and E interacts with
the focused reachable object. Then use F5 to save and F9 to restore, verifying
the saved position/state and capturing the actual game client. Preserve
existing user saves and inspect the full player log for exceptions.

The owner subsequently confirmed the corrected executable loaded, movement
worked and objects were interactable. The preserved actual player log records
successful New Run bootstrap. Those inputs were owner-operated; agent-driven
native input, save/load and a native game capture remain pending desktop
availability. Camera height/in-room readability was reported as a separate
remaining playability problem.
