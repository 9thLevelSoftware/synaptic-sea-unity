// Ported from scripts/systems/save_slot_state.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One row in the save index (ADR-0031). Carries the metadata the menu / autosave policy / cloud adapter
    /// need without re-parsing the slot file.
    /// </summary>
    public class SaveSlotState
    {
        public const string SlotKindManual = "manual";
        public const string SlotKindAuto = "auto";
        public const string SlotKindQuick = "quick";
        public const string SlotKindWorld = "world";
        public static readonly GdArray SlotKinds = GdArray.Of(SlotKindManual, SlotKindAuto, SlotKindQuick, SlotKindWorld);

        public static readonly GdArray ManualSlotIds = GdArray.Of("slot_01", "slot_02", "slot_03", "slot_04", "slot_05", "slot_06");
        public static readonly GdArray AutosaveSlotIds = GdArray.Of("autosave_a", "autosave_b", "autosave_c");
        public const string QuicksaveSlotId = "quicksave";
        public const string WorldSlotId = "world";

        public string SlotId = "";
        public string SlotKind = "";
        public string DisplayName = "";
        public long SynapticSeaSeed = 0;
        public string PlayerClass = "";
        public string CurrentLocation = "";
        public long ObjectiveSequence = 1;
        public double PlayTimeSeconds = 0.0;
        public string SavedAt = "";          // ISO 8601
        public long SavedAtEpoch = 0;        // for sort/backup filename
        public string EmbeddedWorldSlotId = ""; // GDScript `world_slot_id` (renamed: collides with the WORLD_SLOT_ID constant). Non-empty only when embedded in a world slot
        public bool Corrupt = false;
        public bool Frozen = false;          // permadeath freeze (ADR-0032)
        public long PayloadSizeBytes = 0;
        public string SchemaVersion = "";
        // run_id slot-ownership rework: the run_id of whichever run's write last stamped this row.
        public string RunId = "";

        public bool IsWorld() => SlotKind == SlotKindWorld;
        public bool IsManual() => SlotKind == SlotKindManual;
        public bool IsAuto() => SlotKind == SlotKindAuto;
        public bool IsQuick() => SlotKind == SlotKindQuick;

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "slot_id", SlotId },
                { "slot_kind", SlotKind },
                { "display_name", DisplayName },
                { "synaptic_sea_seed", SynapticSeaSeed },
                { "player_class", PlayerClass },
                { "current_location", CurrentLocation },
                { "objective_sequence", ObjectiveSequence },
                { "play_time_seconds", PlayTimeSeconds },
                { "saved_at", SavedAt },
                { "saved_at_epoch", SavedAtEpoch },
                { "world_slot_id", EmbeddedWorldSlotId },
                { "corrupt", Corrupt },
                { "frozen", Frozen },
                { "payload_size_bytes", PayloadSizeBytes },
                { "schema_version", SchemaVersion },
                { "run_id", RunId },
            };
        }

        public static SaveSlotState FromDict(object data)
        {
            if (!(data is GdDict dict)) return null;
            var row = new SaveSlotState();
            row.SlotId = V.Str(dict.Get("slot_id", ""));
            row.SlotKind = V.Str(dict.Get("slot_kind", ""));
            row.DisplayName = V.Str(dict.Get("display_name", ""));
            row.SynapticSeaSeed = V.I64(dict.Get("synaptic_sea_seed", 0L));
            row.PlayerClass = V.Str(dict.Get("player_class", ""));
            row.CurrentLocation = V.Str(dict.Get("current_location", ""));
            row.ObjectiveSequence = V.I64(dict.Get("objective_sequence", 1L));
            row.PlayTimeSeconds = V.F64(dict.Get("play_time_seconds", 0.0));
            row.SavedAt = V.Str(dict.Get("saved_at", ""));
            row.SavedAtEpoch = V.I64(dict.Get("saved_at_epoch", 0L));
            row.EmbeddedWorldSlotId = V.Str(dict.Get("world_slot_id", ""));
            row.Corrupt = V.Bool(dict.Get("corrupt", false));
            row.Frozen = V.Bool(dict.Get("frozen", false));
            row.PayloadSizeBytes = V.I64(dict.Get("payload_size_bytes", 0L));
            row.SchemaVersion = V.Str(dict.Get("schema_version", ""));
            row.RunId = V.Str(dict.Get("run_id", ""));
            return row;
        }

        /// <summary>Coarse validation: rejects null, empty slot_id, or unknown slot_kind.</summary>
        public static bool Validate(SaveSlotState row)
        {
            if (row == null) return false;
            if (string.IsNullOrEmpty(row.SlotId)) return false;
            if (!SlotKinds.Contains(row.SlotKind)) return false;
            return true;
        }
    }
}
