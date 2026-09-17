# UI port notes (Phase 10, `port/ui-panels`)

The 30 files in `scripts/ui/` (Godot `96ecb2b0`) are ported as code-built UI Toolkit presenters under `Assets/_Project/UI/`. They are built to `docs/game/features/ui_presentation_program.md`, not to the Godot layout. Each presenter keeps its Godot model calls, deny paths and public API, PascalCased. Signals become C# events.

## Files

| Godot | Unity |
|---|---|
| `accessibility_settings.gd` | `UI/Presenters/AccessibilitySettings.cs` (pure; `SynapticSea.UI.Presenters` has `noEngineReferences`) |
| `save_load_menu.gd` | `UI/Presenters/SaveLoadMenu.cs` (pure) |
| `menu_coordinator.gd` | `UI/Menus/MenuCoordinator.cs`, `UI/Common/ModalStack.cs`, and `UI/Presenters/SaveSlotScreenModel.cs` (the save/load slot state machine) |
| `menu_panel.gd` | `UI/Menus/MenuPanel.cs` |
| `hallucination_fx_overlay.gd` | No UI presenter. `Runtime/Rendering/HallucinationFx` holds the intensity and `HallucinationRendererFeature` draws it (fed from `SessionEvents.HallucinationFxIntensity`) |
| `objective_tracker.gd` | `UI/Hud/ObjectiveChip.cs` |
| `player_vitals_panel.gd` | `UI/Hud/HudVitalsCluster.cs` + `Meter.cs` |
| `work_action_hud_panel.gd` | `UI/Hud/WorkActionStrip.cs` |
| `hotbar_panel.gd`, `tooltip_panel.gd`, `tutorial_overlay_panel.gd` | `UI/Hud/HudTransients.cs` (`HotbarStrip`, `TooltipCard`, `TutorialBanner`) |
| `inventory_panel.gd`, `inventory_row.gd`, `inventory_drop_zone.gd` | `UI/Panels/InventoryPanel.cs` (rows are `SelectableList` rows; drop zones are pointer-picked elements) |
| `wounds_panel.gd` | `UI/Panels/WoundsPanel.cs` |
| `ship_modification_panel.gd` | `UI/Panels/ShipModificationPanel.cs` |
| `scanner_panel.gd` | `UI/Panels/ScannerPanel.cs` (+ `IScannerHost`, reference `ScannerHost`) |
| `chart_panel.gd` | `UI/Panels/ChartPanel.cs` |
| `recipe_picker_panel.gd` | `UI/Panels/RecipePickerPanel.cs` (+ `IRecipePickerHost`, reference `CraftingStationHost`) |
| `codex_panel.gd` | `UI/Panels/CodexPanel.cs` |
| `achievements_panel.gd`, `skill_tree_panel.gd`, `hub_upgrade_panel.gd`, `class_panel.gd` | `UI/Menus/{Achievements,SkillTree,HubUpgrade,Class}Panel.cs` |
| `audio_log_panel.gd`, `audio_settings_panel.gd` | `UI/Menus/AudioPanels.cs` |
| `language_selector.gd`, `release_badge_overlay.gd`, `credits_screen.gd` | `UI/Menus/RecordScreens.cs` (+ `CreditsOverlay`, the Typography entries for Inter and JetBrains Mono) |
| `run_results_panel.gd` | `UI/Menus/RunResultsPanel.cs` |
| (save_load meta-screen view) | `UI/Menus/SaveLoadScreen.cs` |

Shared building blocks are in `UI/Common/`: `SurfacePanel`, `SelectableList`, `StatusLine`, `SeverityText`, `GlyphChips`, `IUiAudio`/`AudioManagerAdapter`, and `UiInputRouter`. Styles are in `Content/UI/Theme/panels.uss` and `hud.uss`, both imported by `SynapticSea.tss`.

## Session wiring

In play, `Game/PlayableBootstrap` composes the UI and `Game/SessionUiBridge` does the wiring. On the title, `App/Title/TitleScreen` runs the same `MenuCoordinator` in `TitleMode`.

1. **Build the HUD and menu documents.** `PlayableBootstrap.Boot` creates the HUD `UIDocument` (with `HudRoot`) and the menu `UIDocument`, then constructs `SessionUiBridge`. Before the session's ready events fire, `SessionUiBridge.BindSessionEvents(session)` subscribes the HUD to `SessionEvents`: tracker, prompts, weapon line, toasts, inventory and hotbar. Events that arrive before the coordinator exists are replayed once it does.
2. **Build the coordinator.** After the boot, `SessionUiBridge.BuildCoordinator(session, host, audio)` constructs `MenuCoordinator` with Godot's `bind_meta_screens` dependencies.
   - The coordinator gets the session's single `TutorialState` and `SettingsState`.
   - UI sounds go through `SessionUiAudio`, which sits over the session's `SessionAudio` models so bus volumes have one source of truth.
   - It then calls `ConfigureFromData(a11y)`.
3. **Adopt the stored preferences.** `user://settings.json` (`UserSettingsStore`), or the app's in-memory settings, is loaded with `LoadSettingsSummary` before any handler is subscribed, so nothing re-saves defaults. The adopted values are:
   - the stored bus volumes and mutes, applied to the session audio;
   - accessibility, applied to the HUD at mount and on every change;
   - `hold_to_tap`, which `RunSession.HoldToWorkEnabled` reads live from the session's `SettingsState`.
   The bridge keeps a copy of the preferences and re-adopts it after every load, so settings in a save never overwrite the player's preferences.
4. **Mount.** `coordinator.Root` goes into the menu document and `Hud.Mount(coordinator)` runs. World labels (`WorldLabelLayer`) draw into `Hud.WorldLabelLayer` at the current text scale.
5. **Route input.** `PlayableBootstrap.Update` calls `SessionUiBridge.Tick()`, which calls `UiInputRouter.Tick()`, refreshes the vitals cluster and, every 250 ms, refreshes the status-effect icons.
   - `PanelToggleRequested` opens or closes the LIVE inspection panels. A denied toggle raises a toast ("No web chart", "Ship modification unavailable").
   - `DevShortcutRequested` (F5, F6, F9) is compiled out of `SS_BUILD_RELEASE` builds.
   - The host's gates come from the modal stack: `RunSessionHost.SimulationPaused = Stack.SimulationPaused`, `GameplayInputBlocked = Stack.BlocksGameplay` and `MotionReduce`.
   - `PlayerController` raises attack, reload, hotbar 1–3, interact press and release, field craft and movement. `RunSessionHost` refuses them while `GameplayInputAllowed` is false.
6. **Inspection panels.** Inventory, Wounds, Scanner, Ship modification, Chart and Recipe picker are opened through `coordinator.OpenInspection(panel)`. Each panel's `PanelClosed` calls `NotifyInspectionClosed`. Wound treatment goes through `RunSession.TryBandageWound` / `TryTreatWound` (`SessionWoundHost`), so the item is consumed and the SFX and training events fire.
7. **Coordinator events:**
   - `SaveRequested`, `LoadRequested`, `WorldLoadRequested`: `RequestSave` / `RequestLoad`, then the preferences are re-adopted.
   - `SlotSnapshotLoaded`: `ApplyManualSlot`.
   - `QuitRequested`: `QuitToTitle`. `SaveAndExitRequested`: `SaveAndExit`.
   - `SettingsChanged(summary)`: `ApplyUiSettingsSummary`, accessibility applied to the scene, and the merged state persisted through `AppServices.ApplySettings` (`user://settings.json`).
   - `LanguageChanged(id)`: persisted through the settings file. Godot's `LocalizationCatalog` only has `en` and no UI string reads it.
8. **Combat feedback.** `RunSessionHost.PlayerDamaged` (from `ThreatRuntime.ThreatAttacked`, via `ThreatPlaceholderView.PlayerHit`) calls `HudRoot.ShowDamage`: the damage indicator and flash.
9. **Pause and run end.** `coordinator.Stack.SimulationPaused` suspends the session tick. It is true while the pause stack or the run results are open, and false under LIVE inspection, where gameplay input is still blocked. When the run ends (`PlayableSliceCompleted`: death, extract, or slice complete), `SessionUiBridge.ShowRunResults` opens `RunResultsPanel` as a TERMINAL surface with a seed · biome · difficulty context line (Godot `title_main.gd` `_show_run_results`). Pause quit (`ReturnToTitleRequested`) does not replace that panel with a silent Title dump.
   - The panel shows a severity banner (symbol, wording and colour), a block of 44 px label/value rows (outcome, cause, time survived and only the counters the run tracked), the epitaph in its own block on death, and the context line (`RunResultsPanel.SetContextLine`). Styles are the `.ss-results*` rules in `panels.uss`. `BodyText` keeps Godot's line list.
   - Return to Title stores `RunReturnInfo`, and the title shows the last-run line.
   - New Run starts another Milestone A hub boot (slice seed / biome / difficulty) with the same class.
10. **Hallucination FX.** `HallucinationRendererFeature` reads `Runtime/Rendering/HallucinationFx.Intensity` and `MotionReduce`. `HallucinationView` (in `ThreatPlaceholderView.cs`) forwards `SessionEvents.HallucinationFxIntensity` to it. There is no UI-side presenter.

`PlayableScenePlayModeTests` covers this path end to end:
- the gamepad inventory → pause → resume journey;
- stored settings applied in play, and a change saving the merged state;
- settings persisting from Title through play and the pause menu back to Title;
- combat, reload and hotbar keys under the modal stack;
- a real threat's hit reaching the HUD damage indicator.

## Spec rules and where they are enforced

- **Zoning, coverage and protected areas.** The zones are set in `hud.uss` and enforced by `HudLayoutTests`. That test builds a real UI Toolkit layout: the HUD is mounted in a runtime panel backed by a RenderTexture, using the project `PanelSettings` and theme. It checks 1280×720, 1920×1080 and 1280×800 at 1×, 1.5× and 2×.
  - It measures the union of the backed regions. The persistent HUD must be ≤20% at 1× and ≤30% at 1.5×/2×. With transients it must be ≤25% and ≤35%.
  - Nothing may enter the central 40%×45% or the corridor at x 35–65%, y ≥70%.
  - Nothing may be clipped, and the chip and the column must not overlap.
  - Text must be ≥18 px for body and ≥16 px for metadata, multiplied by the text scale.
- **Large text.** Large text reflows; it is never shrunk. At 1.5× and 2×, `HudRoot` applies disclosure:
  - Danger chips stay. Lower-ranked chips collapse into "+N danger" or "+N caution".
  - Quick-use slots other than the selected one show only their number.
  - The work hint drops.
  - The objective chip moves beside the column, still inside the top band.
  - At most 2 transients show at 1×, and 1 at 1.5×/2×.
- **Visual states.** Every surface carries a LIVE or PAUSED badge. Focus is shown by an outline. Rows and controls are 44 px (`InspectionSurface...Rows44px` checks this). Severity always pairs a symbol with wording (`SeverityText`; meters show a symbol).
- **Navigation.**
  - Covered surfaces are disabled.
  - Initial focus is explicit.
  - `SelectableList` and `MenuPanel` handle `NavigationMoveEvent` and `NavigationSubmitEvent`.
  - Lists restore focus to the selected id after a refresh.
  - A submenu's Back restores focus to the item that opened it, using the item token.
  - Popping the stack restores the uncovered surface's focus token and swallows the held Accept.

## Deviations from Godot

- **Layout follows the spec.**
  - The Godot tracker contents (controls legend, system lines) move behind `ObjectiveChip.GetHudText()`, and the interaction prompt becomes a transient context prompt.
  - The bottom-centre hotbar moves into the cluster.
  - The work panel moves above the cluster.
  - Codex is LIVE when it is opened from play.
- **Keyboard shortcuts.** Godot's `MenuState` resets focus to index 0 on Back. The coordinator restores focus to the submenu's opening item instead. `MenuState` itself is unchanged.
- **Wounds.** Rows are ranked by severity (critical first). After a treatment, selection follows the wound's id.
- **Inventory.**
  - The right-click `PopupMenu` becomes an always-visible action row, and the `AcceptDialog` split becomes an inline stepper.
  - Deny paths now show a caution line as well as playing the SFX.
  - Submit runs the primary action: transfer in transfer mode, otherwise equip, otherwise use.
- **Audio log.** Moving the cursor no longer plays the entry, as Godot's `item_selected` did. Submit or Play does. Stop is mirrored in `AudioManagerAdapter`; in play, `SessionUiAudio` forwards it to `AudioManager.StopVoiceLog`.
- **Save/load result.** The save/load confirm result no longer carries `snapshot`, because `GdDict` holds only Variant leaves. The snapshot is exposed as `SaveSlotScreenModel.LastLoadedSnapshot` and through the `SlotSnapshotLoaded` event.
- **Build info.** The release badge also shows the version and store.
- **Text scale.** `AccessibilitySettings` reads the env var only; Unity has no Godot project settings. A scale between steps reflows up to the next step, never down.

## Open items

- `HudRoot` polls transient arbitration every 100 ms. An event from `TooltipCard` and `TutorialBanner` would be cleaner.
- The router only dispatches Menu-map navigation when no element has focus. UI Toolkit navigation itself comes from the EventSystem with `InputSystemUIInputModule` that `AppServices` creates.
- The colour-blind palettes in `panels.uss` are provisional. They need contrast measurement on real backgrounds.
- `ScannerHost` and `CraftingStationHost` are reference adapters. The session uses `SessionScannerHost` and `SessionRecipeHost` (`Game/SessionUiAudio.cs`) and owns the field-craft, salvage and hydroponics branches.
