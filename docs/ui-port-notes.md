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

1. **Build the coordinator.** Construct `MenuCoordinator` with the 14 dependencies from Godot's `bind_meta_screens`, in the same order. Wrap the Runtime `AudioManager` in `AudioManagerAdapter`. Then call `ConfigureFromData(a11y)`, or call `Configure(...)` with explicit catalogs.
2. **Mount the UI.** Add `coordinator.Root` to the menu `UIDocument` (`PanelSettings_Menu`). Build `HudRoot` on the HUD document and call `hud.Mount(coordinator)`.
3. **Route input.** Each frame, call `UiInputRouter.Tick()`, constructed as `new UiInputRouter(input, coordinator.Stack, coordinator.HandleUiInput)`. It disables the Player map while any surface is open. Handle `PanelToggleRequested` and `DevShortcutRequested` in the session.
4. **Open inspection panels.** Open the panel (`inventory.OpenTransfer(...)`, `wounds.Open()`, and so on), then call `coordinator.OpenInspection(panel)`. Subscribe `panel.PanelClosed += () => coordinator.NotifyInspectionClosed(panel)`.
5. **Handle the events:**
   - `ModalOpened(menuId)` and `ModalClosed(menuId)`
   - `SaveRequested`, `LoadRequested`, `QuitRequested`, `SaveAndExitRequested`
   - `SettingsChanged(summary)`: persist it, and push captions to the SFX router (ADR-0044)
   - `MetaScreenConfirmed(result)`
   - `SlotSnapshotLoaded(slotId, snapshot)`: apply the manual slot
   - `WorldLoadRequested`: run the world load
   - `LanguageChanged(id)`
6. **Pause the simulation.** Use `coordinator.Stack.SimulationPaused` to suspend the simulation. It is true while the pause stack or run results are open. It is false for LIVE inspection, where gameplay is still blocked.
7. **Hallucination FX.** `HallucinationRendererFeature` reads `Runtime/Rendering/HallucinationFx.Intensity` and `MotionReduce`. `ThreatPlaceholderView` forwards `SessionEvents.HallucinationFxIntensity` to it; there is no UI-side presenter.

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
- **Audio log.** Moving the cursor no longer plays the entry, as Godot's `item_selected` did. Submit or Play does. Stop is mirrored in `AudioManagerAdapter`, because the Runtime has no way to clear the current voice log.
- **Save/load result.** The save/load confirm result no longer carries `snapshot`, because `GdDict` holds only Variant leaves. The snapshot is exposed as `SaveSlotScreenModel.LastLoadedSnapshot` and through the `SlotSnapshotLoaded` event.
- **Build info.** The release badge also shows the version and store.
- **Text scale.** `AccessibilitySettings` reads the env var only; Unity has no Godot project settings. A scale between steps reflows up to the next step, never down.

## Open items

- `HudRoot` polls transient arbitration every 100 ms. An event from `TooltipCard` and `TutorialBanner` would be cleaner.
- UI Toolkit runtime navigation in the player needs an EventSystem and `InputSystemUIInputModule`, or the project-wide UI actions. That belongs in the session scene. The router only dispatches Menu-map navigation when no element has focus.
- The colour-blind palettes in `panels.uss` are provisional. They need contrast measurement on real backgrounds.
- The Runtime `AudioManager` needs `StopVoiceLog()`. `ScannerHost` and `CraftingStationHost` are reference adapters; the session owns the field-craft, salvage and hydroponics branches.
- The PlayMode gamepad journey from the port plan is not written here: Title, Settings, Back, then Inventory with transfer and close, then Pause over Inventory and Resume.
