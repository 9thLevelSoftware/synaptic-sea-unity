// Launch contract between the Title scene and the Playable scene (replaces title_main.gd's in-process
// MAIN_SCENE.instantiate() + request_load() + apply_ui_settings_summary() handoff @ 96ecb2b0).
using SynapticSea.Core.Variant;

namespace SynapticSea.Runtime.Session
{
    /// <summary>How the Playable scene should start the run.</summary>
    public enum RunLaunchMode
    {
        /// <summary>Fresh run (Godot title "New Run"): build the default start and do not load.</summary>
        NewRun,
        /// <summary>Continue the world save (Godot title "Continue": <c>request_load()</c> on the world slot).</summary>
        Continue,
        /// <summary>Load one manual/auto/quick slot picked on the Save / Load screen (<see cref="RunLaunchRequest.SlotId"/>).</summary>
        LoadSlot,
    }

    /// <summary>
    /// What the Title scene asks the Playable scene to do. A plain object: the title stores it in <see cref="Pending"/>
    /// and then loads <see cref="PlayableSceneName"/>; the playable bootstrap calls <see cref="Consume"/> once.
    /// When <see cref="Pending"/> is null (Playable opened directly in the editor) the bootstrap should behave as
    /// <see cref="NewRun"/> with the defaults.
    /// </summary>
    public sealed class RunLaunchRequest
    {
        public const string PlayableSceneName = "Playable";
        public const string TitleSceneName = "Title";

        /// <summary>Godot's fixed default start (<c>RunSession.DEFAULT_LAYOUT_PATH</c> = smoke/seed_000017).</summary>
        public const long DefaultSeed = 17;
        /// <summary>Milestone A default biome (ui_presentation_program.md: "fixed default start, breach_field/standard").</summary>
        public const string DefaultBiomeId = "breach_field";
        public const string DefaultDifficultyId = "standard";
        /// <summary>Godot <c>starting_class_id</c> export default.</summary>
        public const string DefaultClassId = "engineer";
        /// <summary>The world save slot Continue loads (<c>TitleSaveQuery.WorldSlotId</c>).</summary>
        public const string WorldSlotId = "world";

        public RunLaunchMode Mode = RunLaunchMode.NewRun;

        /// <summary>Slot id for <see cref="RunLaunchMode.LoadSlot"/>; <see cref="WorldSlotId"/> for Continue; "" for a new run.</summary>
        public string SlotId = "";

        /// <summary>Layout seed. <see cref="DefaultSeed"/> with <see cref="DefaultBiomeId"/> means Godot's fixed default start.</summary>
        public long Seed = DefaultSeed;

        public string BiomeId = DefaultBiomeId;

        /// <summary>Difficulty profile id (standard / hardened / deep_dive), from the title settings.</summary>
        public string DifficultyId = DefaultDifficultyId;

        /// <summary>Starting class: the meta-progression selected class, else <see cref="DefaultClassId"/>.</summary>
        public string ClassId = DefaultClassId;

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

        public static RunLaunchRequest ContinueWorld() => new RunLaunchRequest { Mode = RunLaunchMode.Continue, SlotId = WorldSlotId };

        public static RunLaunchRequest LoadSlot(string slotId) => new RunLaunchRequest { Mode = RunLaunchMode.LoadSlot, SlotId = slotId ?? "" };

        public override string ToString() =>
            $"RunLaunchRequest(mode={Mode}, slot={SlotId}, seed={Seed}, biome={BiomeId}, difficulty={DifficultyId}, class={ClassId}, settings={(SettingsSummary != null ? "dirty" : "untouched")})";
    }

    /// <summary>
    /// What the Playable scene reports back before loading the Title scene again (Godot title_main.gd surfaced
    /// <c>_last_boot_error</c>, <c>_last_run_outcome</c> and <c>_last_run_progress</c> under the menu). Optional: the
    /// title shows whichever fields are non-empty and then clears them.
    /// </summary>
    public static class RunReturnInfo
    {
        /// <summary>Boot/reload failure reason ("Load failed: …").</summary>
        public static string LastFailureReason = "";
        /// <summary>Completed-run outcome ("Last run: …").</summary>
        public static string LastRunOutcome = "";
        /// <summary>"objectives n/m" ("Progress: …").</summary>
        public static string LastRunProgress = "";

        public static void Clear()
        {
            LastFailureReason = "";
            LastRunOutcome = "";
            LastRunProgress = "";
        }
    }
}
