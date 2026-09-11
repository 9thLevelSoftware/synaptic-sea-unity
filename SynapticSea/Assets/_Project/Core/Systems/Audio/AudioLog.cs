// Ported from scripts/audio/audio_log.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Data-only registry of voice-log entries (REQ-AU-006, ADR-0029). Each entry has id, label, transcript,
    /// clip_path, duration, volume_db. Callers look entries up by id and never mutate them.
    /// </summary>
    public sealed class AudioLog
    {
        public static readonly GdArray DEFAULT_ENTRIES = GdArray.Of(
            new GdDict
            {
                { "id", "log.beacon_01" },
                { "label", "Beacon — Distress 01" },
                { "transcript", "Mayday. Repeat, mayday. Reactor is critical. Hull integrity failing." },
                { "clip_path", "res://data/audio/voice/log_beacon_01.ogg" },
                { "duration", 5.0 },
                { "volume_db", -3.0 },
            },
            new GdDict
            {
                { "id", "log.beacon_02" },
                { "label", "Beacon — Distress 02" },
                { "transcript", "Biomatter incursion in sector four. Sealing the bulkhead. If you can hear this..." },
                { "clip_path", "res://data/audio/voice/log_beacon_02.ogg" },
                { "duration", 6.0 },
                { "volume_db", -3.0 },
            },
            new GdDict
            {
                { "id", "log.pulse_01" },
                { "label", "Pulse — Biomatter 01" },
                { "transcript", "Ambient field resonance detected. Approaching pulse from aft quadrant." },
                { "clip_path", "res://data/audio/voice/log_pulse_01.ogg" },
                { "duration", 4.0 },
                { "volume_db", -6.0 },
            },
            new GdDict
            {
                { "id", "log.groan_01" },
                { "label", "Groan — Hull 01" },
                { "transcript", "Structural groans increasing. Recommend abandoning lower decks." },
                { "clip_path", "res://data/audio/voice/log_groan_01.ogg" },
                { "duration", 3.5 },
                { "volume_db", -6.0 },
            },
            new GdDict
            {
                { "id", "log.tutorial_pickup" },
                { "label", "Tutorial — Pickup" },
                { "transcript", "Acquired a portable oxygen pump. Use it near sealed bulkheads to extend your oxygen supply." },
                { "clip_path", "res://data/audio/voice/log_tutorial_pickup.ogg" },
                { "duration", 4.5 },
                { "volume_db", -3.0 },
            },
            new GdDict
            {
                { "id", "log.tutorial_calibrator" },
                { "label", "Tutorial — Calibrator" },
                { "transcript", "Junction calibrator acquired. Apply it to a damaged junction to skip a repair step." },
                { "clip_path", "res://data/audio/voice/log_tutorial_calibrator.ogg" },
                { "duration", 5.0 },
                { "volume_db", -3.0 },
            });

        readonly GdDict _entries = new GdDict();

        public AudioLog()
        {
            foreach (object entryVariant in DEFAULT_ENTRIES)
            {
                if (!(entryVariant is GdDict entry)) continue;
                object idValue = entry.Get("id", null);
                if (idValue == null) continue;
                // GDScript stores the read-only const dictionary itself; a per-instance deep copy keeps the shared
                // static table from being mutated through a returned reference.
                _entries[V.Str(idValue)] = entry.DeepCopy();
            }
        }

        /// <summary>Looks up an entry by id; an empty dictionary when not found.</summary>
        public GdDict GetEntry(string entryId) => _entries.Get(entryId, null) as GdDict ?? new GdDict();

        public bool HasEntry(string entryId) => _entries.Has(entryId);

        public GdArray ListEntryIds() => new GdArray(_entries.Keys);

        public GdArray GetAllEntries() => new GdArray(_entries.Values);
    }
}
