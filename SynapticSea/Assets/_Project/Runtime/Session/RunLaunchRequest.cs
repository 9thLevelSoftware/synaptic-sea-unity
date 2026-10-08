// Launch contract between the Title scene and the Playable scene (replaces title_main.gd's in-process
// MAIN_SCENE.instantiate() + request_load() + apply_ui_settings_summary() handoff @ 96ecb2b0).
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Runtime.Session
{
    /// <summary>How the Playable scene should start the run.</summary>
    public enum RunLaunchMode
    {
        /// <summary>Fresh run (Godot title "New Run"): Milestone A hub golden <c>coherent_ship_001</c> for the slice defaults.</summary>
        NewRun,
        /// <summary>Continue the world save (Godot title "Continue": <c>request_load()</c> on the world slot).</summary>
        Continue,
        /// <summary>Load one manual/auto/quick slot picked on the Save / Load screen (<see cref="RunLaunchRequest.SlotId"/>).</summary>
        LoadSlot,
    }

    /// <summary>
    /// What the Title scene asks the Playable scene to do. A plain object: the title stores it in <see cref="Pending"/>
    /// and then loads <see cref="PlayableSceneName"/>; the playable bootstrap calls <see cref="Consume"/> once.
    /// When <see cref="Pending"/> is null (Playable opened directly in the editor) the bootstrap behaves as
    /// <see cref="NewRun"/> with the defaults, through the same Milestone A hub path.
    /// </summary>
    public sealed class RunLaunchRequest
    {
        public const string PlayableSceneName = "Playable";
        public const string TitleSceneName = "Title";

        /// <summary>
        /// A direct-open run's seed (and the golden hub's own blueprint seed). The Milestone A hub is golden
        /// <c>coherent_ship_001</c> for every seed. The title's New Run setup starts on a random seed and Results "New Run"
        /// rolls a fresh one; the seed drives the world, markers and first wreck, never the home.
        /// </summary>
        public const long DefaultSeed = 17;
        /// <summary>Milestone A default biome (ui_presentation_program.md: "fixed default start, breach_field/standard").</summary>
        public const string DefaultBiomeId = "breach_field";
        public const string DefaultDifficultyId = "standard";
        /// <summary>Godot <c>starting_class_id</c> export default.</summary>
        public const string DefaultClassId = "engineer";
        /// <summary>The world save slot Continue loads (<c>TitleSaveQuery.WorldSlotId</c>).</summary>
        public const string WorldSlotId = "world";
        /// <summary>The golden layout directory tests request explicitly (<see cref="GoldenShip"/>).</summary>
        public const string GoldenShipDir = "res://data/procgen/golden/coherent_ship_001/";

        public RunLaunchMode Mode = RunLaunchMode.NewRun;

        /// <summary>Slot id for <see cref="RunLaunchMode.LoadSlot"/>; <see cref="WorldSlotId"/> for Continue; "" for a new run.</summary>
        public string SlotId = "";

        /// <summary>Explicit development/fixture opt-in. Ordinary launch/save/Continue remains on the legacy path. Default off; activation scheduled for Phase 5.5 (see docs/design/decisions.md, Phase 0.3 decision 3). The one sanctioned exception to the no-default-off-flags rule.</summary>
        public bool EnableComponentIntegration;
        GdDict _selectedSaveGeneration;
        /// <summary>Owned exact generation handle; never inferred from the presence of a generation on disk.</summary>
        public GdDict SelectedSaveGeneration
        {
            get => _selectedSaveGeneration?.DeepCopy();
            set => _selectedSaveGeneration = value?.DeepCopy();
        }

        /// <summary>The New Run seed: any seed in [MilestoneALaunch.MinSeed, MaxSeed] boots the Milestone A hub with a seeded world.</summary>
        public long Seed = DefaultSeed;

        /// <summary>Biome id (<c>data/procgen/biomes/&lt;id&gt;.json</c>).</summary>
        public string BiomeId = DefaultBiomeId;

        /// <summary>Difficulty profile id (standard / hardened / deep_dive).</summary>
        public string DifficultyId = DefaultDifficultyId;

        /// <summary>Game seconds per real second (Phase 1.1). 1.0 keeps game time equal to real time.</summary>
        public double TimeScale = WorldClock.DefaultScale;

        /// <summary>Starting class: the meta-progression selected class, else <see cref="DefaultClassId"/>.</summary>
        public string ClassId = DefaultClassId;

        /// <summary>
        /// Test-only: boot this pre-authored layout instead of generating one (e.g. the golden
        /// <c>coherent_ship_001</c>). The gameplay slice and blueprint sidecar are read from the same directory
        /// (<c>gameplay_slice.json</c>, <c>blueprint.json</c>). Empty = generate.
        /// </summary>
        public string LayoutOverridePath = "";

        /// <summary>
        /// The title's <c>SettingsState</c> summary when the player changed settings at the title (Godot's
        /// <c>_settings_dirty</c> handoff: <c>apply_ui_settings_summary</c> after any load). Null when untouched, so a
        /// loaded run keeps its own settings. The same preferences are also persisted to <c>user://settings.json</c>.
        /// </summary>
        public GdDict SettingsSummary;

        /// <summary>The request the next Playable scene load should honour; null when none.</summary>
        public static RunLaunchRequest Pending { get; set; }

        /// <summary>Returns <see cref="Pending"/> and clears it.</summary>
        public static RunLaunchRequest Consume()
        {
            RunLaunchRequest request = Pending;
            Pending = null;
            return request;
        }

        public static RunLaunchRequest NewRun() => new RunLaunchRequest { Mode = RunLaunchMode.NewRun };

        /// <summary>A New Run from the title setup. <paramref name="timeScale"/> defaults to 60 (1 real minute = 1 game hour); 1.0 is the real-time "off" pacing.</summary>
        public static RunLaunchRequest NewRun(long seed, string biomeId, string difficultyId, double timeScale = WorldClock.DefaultNewRunScale) =>
            new RunLaunchRequest { Mode = RunLaunchMode.NewRun, Seed = seed, BiomeId = biomeId ?? "", DifficultyId = difficultyId ?? DefaultDifficultyId, TimeScale = timeScale };

        /// <summary>Test-only new run on the golden <c>coherent_ship_001</c> layout (no generation).</summary>
        public static RunLaunchRequest GoldenShip() => new RunLaunchRequest { Mode = RunLaunchMode.NewRun, LayoutOverridePath = GoldenShipDir + "layout.json", BiomeId = "" };

        public static RunLaunchRequest ContinueWorld() => new RunLaunchRequest { Mode = RunLaunchMode.Continue, SlotId = WorldSlotId };

        public static RunLaunchRequest LoadSlot(string slotId) => new RunLaunchRequest { Mode = RunLaunchMode.LoadSlot, SlotId = slotId ?? "" };

        public static RunLaunchRequest DiagnosticNewRun() => new RunLaunchRequest { EnableComponentIntegration = true };

        public static RunLaunchRequest DiagnosticContinue(GdDict selectedGeneration = null) =>
            new RunLaunchRequest { Mode = RunLaunchMode.Continue, SlotId = WorldSlotId, EnableComponentIntegration = true, SelectedSaveGeneration = selectedGeneration };

        public static RunLaunchRequest DiagnosticLoadSlot(string slotId, GdDict selectedGeneration = null) =>
            new RunLaunchRequest { Mode = RunLaunchMode.LoadSlot, SlotId = slotId ?? "", EnableComponentIntegration = true, SelectedSaveGeneration = selectedGeneration };

        public override string ToString() =>
            $"RunLaunchRequest(mode={Mode}, slot={SlotId}, seed={Seed}, biome={BiomeId}, difficulty={DifficultyId}, class={ClassId}" +
            (LayoutOverridePath.Length != 0 ? $", layout={LayoutOverridePath}" : "") + $", settings={(SettingsSummary != null ? "dirty" : "untouched")})";
    }

    /// <summary>
    /// What the Playable scene reports back before loading the Title scene again (Godot title_main.gd surfaced
    /// <c>_last_boot_error</c>, <c>_last_run_outcome</c> and <c>_last_run_progress</c> under the menu). The title shows
    /// whichever fields are non-empty and then clears them.
    /// </summary>
    public static class RunReturnInfo
    {
        /// <summary>Boot/reload failure reason ("Load failed: …").</summary>
        public static string LastFailureReason = "";
        /// <summary>An explicitly refused diagnostic import can reopen the unchanged original legacy slot.</summary>
        public static string OriginalSaveSlotId = "";
        /// <summary>Completed-run outcome ("Last run: …"): death / extraction / abort.</summary>
        public static string LastRunOutcome = "";
        /// <summary>"objectives n/m" ("Progress: …").</summary>
        public static string LastRunProgress = "";
        /// <summary>"seed 17 · breach_field · standard" for the last run (shown with the outcome).</summary>
        public static string LastRunContext = "";
        /// <summary>"12:34" run time of the last finished run ("" when unknown).</summary>
        public static string LastRunTime = "";

        public static void Clear()
        {
            LastFailureReason = "";
            OriginalSaveSlotId = "";
            LastRunOutcome = "";
            LastRunProgress = "";
            LastRunContext = "";
            LastRunTime = "";
        }
    }
}
