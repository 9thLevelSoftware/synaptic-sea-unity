# Steam platform layer (inert by default)

This folder is the plan's Phase 12 Steam stretch. It compiles **only** when the `SS_STEAM` scripting define is set
(`SynapticSea.Platform.Steam.asmdef`, `defineConstraints`). Normal dev, demo and release builds contain none of it,
and no Steam binaries are committed.

## What it does
- `SteamPlatform` starts itself after the first scene loads (never in batch mode). When
  `data/release/build_metadata.json` has `achievements_supported: true`, it reads the numeric app id from
  `steam_appid.txt` and calls `SteamClient.Init(appId, false)`. It pumps `SteamClient.RunCallbacks()` every frame and
  calls `SteamClient.Shutdown()` on exit.
- `SteamAchievements` binds to each booted `RunSession`'s `AchievementState.Unlocked` event and calls
  `new Steamworks.Data.Achievement(id).Trigger()`. Unlocks restored before the binding are replayed.
- Achievement API names are the catalog ids verbatim. The rule is in `Core/Systems/Progression/PlatformAchievementIds.cs`,
  and `ProgressionModelTests.CatalogIds_AreValidSteamApiNames` checks every id in `achievement_catalog.json`.

Steamworks achievement API names to create, one per catalog entry:
`first_breath`, `first_repair`, `first_loot`, `objective_complete`, `reactor_stabilized`, `extracted`,
`junction_calibrator_used`, `all_systems_restored`.

## Enabling it
1. Download a Facepunch.Steamworks release from https://github.com/Facepunch/Facepunch.Steamworks/releases. It is
   not a UPM package, and a git dependency would pull the native Steam libraries into every build.
2. Copy the plugin into `Assets/_Project/Platform/Steam/Plugins/` (gitignored):
   - Windows: `Facepunch.Steamworks.Win64.dll` and `steam_api64.dll` (import settings: Editor + Windows x86_64).
   - macOS / Linux: `Facepunch.Steamworks.Posix.dll` plus `libsteam_api.dylib` or `libsteam_api.so` (that platform only).
3. Player Settings, Scripting Define Symbols: add `SS_STEAM` for the Standalone target.
4. Put `steam_appid.txt` containing the app id (`480`, Spacewar, while testing) next to the built executable, and in
   `SynapticSea/` for editor play. Both locations are gitignored.
5. Build as usual (`pwsh tools/build.ps1 -Kind release`). Uploads go through SteamPipe (`steamcmd`), which is not
   scripted; `tools/publish-itch.ps1` covers itch.io only.
