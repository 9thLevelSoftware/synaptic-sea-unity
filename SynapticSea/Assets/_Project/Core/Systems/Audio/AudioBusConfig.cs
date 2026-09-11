// Ported from scripts/systems/audio_bus_config.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Audio bus layout (REQ-AU-002, ADR-0029): seven buses (master, sfx, music, voice, ui, ambient, meta), each with
    /// an id, a parent id, a dB volume clamped to [-60, 0], and a muted flag. Pure data + schema validation; the
    /// AudioManager pushes the volumes into the live mixer.
    /// </summary>
    /// <remarks>
    /// In Godot this is a <c>Resource</c> with an <c>@export var buses</c>, loaded from
    /// <c>res://data/audio/audio_bus_config.tres</c>. Here it is a plain data class; <see cref="LoadDefaultResource"/>
    /// reproduces that .tres (its values are hardcoded because .tres files are not shipped as StreamingAssets data).
    /// </remarks>
    public sealed class AudioBusConfig
    {
        public const double MIN_DB = -60.0;
        public const double MAX_DB = 0.0;

        // Default per-bus volumes (dB): the values every playable scene boots with.
        public const double DEFAULT_MASTER_DB = 0.0;
        public const double DEFAULT_SFX_DB = -3.0;
        public const double DEFAULT_MUSIC_DB = -6.0;
        public const double DEFAULT_VOICE_DB = -3.0;
        public const double DEFAULT_UI_DB = -6.0;
        public const double DEFAULT_AMBIENT_DB = -9.0;
        public const double DEFAULT_META_DB = -6.0;

        /// <summary>The Godot resource path this layout comes from.</summary>
        public const string RESOURCE_PATH = "res://data/audio/audio_bus_config.tres";

        /// <summary>Bus records: {"id", "parent_id", "volume_db", "muted"} (GDScript <c>@export var buses: Array</c>).</summary>
        public GdArray Buses = new GdArray();

        bool _validated = false;

        /// <summary>A default bus layout matching ADR-0029 (GDScript <c>static func make_default()</c>).</summary>
        public static AudioBusConfig MakeDefault()
        {
            var cfg = new AudioBusConfig();
            cfg.Buses = new GdArray
            {
                Bus(AudioEventSeam.BUS_MASTER, "", DEFAULT_MASTER_DB),
                Bus(AudioEventSeam.BUS_SFX, AudioEventSeam.BUS_MASTER, DEFAULT_SFX_DB),
                Bus(AudioEventSeam.BUS_MUSIC, AudioEventSeam.BUS_MASTER, DEFAULT_MUSIC_DB),
                Bus(AudioEventSeam.BUS_VOICE, AudioEventSeam.BUS_MASTER, DEFAULT_VOICE_DB),
                Bus(AudioEventSeam.BUS_UI, AudioEventSeam.BUS_MASTER, DEFAULT_UI_DB),
                Bus(AudioEventSeam.BUS_AMBIENT, AudioEventSeam.BUS_MASTER, DEFAULT_AMBIENT_DB),
                Bus(AudioEventSeam.BUS_META, AudioEventSeam.BUS_MASTER, DEFAULT_META_DB),
            };
            cfg._validated = cfg.Validate();
            return cfg;
        }

        /// <summary>
        /// Equivalent of <c>load("res://data/audio/audio_bus_config.tres")</c> @ 96ecb2b0: the .tres sets only
        /// <c>buses</c> (no validation runs on load, so <see cref="IsValidated"/> starts false, as in Godot).
        /// </summary>
        public static AudioBusConfig LoadDefaultResource()
        {
            var cfg = new AudioBusConfig();
            cfg.Buses = new GdArray
            {
                Bus("master", "", 0.0),
                Bus("sfx", "master", -3.0),
                Bus("music", "master", -6.0),
                Bus("voice", "master", -3.0),
                Bus("ui", "master", -6.0),
                Bus("ambient", "master", -9.0),
                Bus("meta", "master", -6.0),
            };
            return cfg;
        }

        static GdDict Bus(string id, string parentId, double volumeDb) =>
            new GdDict { { "id", id }, { "parent_id", parentId }, { "volume_db", volumeDb }, { "muted", false } };

        /// <summary>
        /// Validates the bus layout. When <paramref name="emitErrors"/> is true, the first offending bus is reported
        /// through <c>CoreServices.Log.Error</c> (GDScript <c>push_error</c>).
        /// </summary>
        public bool Validate(bool emitErrors = true)
        {
            if (Buses == null) return ValidationFail("AudioBusConfig: buses is null", emitErrors);
            if (Buses.IsEmpty) return ValidationFail("AudioBusConfig: buses is empty (expected 7 entries)", emitErrors);
            var seenIds = new GdDict();
            bool masterSeen = false;
            for (int i = 0; i < Buses.Count; i++)
            {
                object bus = Buses[i];
                if (!(bus is GdDict busDict))
                    return ValidationFail("AudioBusConfig: bus[" + GdString.FormatInt(i) + "] must be a Dictionary, got type " + GdString.FormatInt(GodotTypeId(bus)), emitErrors);
                object idValue = busDict.Get("id");
                if (idValue == null || (idValue is string ids && ids.Length == 0))
                    return ValidationFail("AudioBusConfig: bus[" + GdString.FormatInt(i) + "] has empty id", emitErrors);
                string idStr = V.Str(idValue);
                if (seenIds.Has(idStr))
                    return ValidationFail("AudioBusConfig: duplicate bus id '" + idStr + "'", emitErrors);
                seenIds[idStr] = true;
                object parentValue = busDict.Get("parent_id");
                string parentStr = "";
                if (parentValue != null) parentStr = V.Str(parentValue);
                double volumeDb = V.F64(busDict.Get("volume_db", 0.0));
                if (double.IsNaN(volumeDb) || double.IsInfinity(volumeDb))
                    return ValidationFail("AudioBusConfig: bus[" + GdString.FormatInt(i) + "] '" + idStr + "' has non-finite volume_db=" + V.Str(volumeDb), emitErrors);
                if (volumeDb < MIN_DB || volumeDb > MAX_DB)
                    return ValidationFail("AudioBusConfig: bus[" + GdString.FormatInt(i) + "] '" + idStr + "' volume_db=" + V.Str(volumeDb) + " out of range [" + V.Str(MIN_DB) + ", " + V.Str(MAX_DB) + "]", emitErrors);
                // Master must have no parent; every other bus must parent to master.
                if (idStr == AudioEventSeam.BUS_MASTER)
                {
                    masterSeen = true;
                    if (parentStr.Length != 0)
                        return ValidationFail("AudioBusConfig: master bus must have empty parent_id, got '" + parentStr + "'", emitErrors);
                }
                else if (parentStr != AudioEventSeam.BUS_MASTER)
                {
                    return ValidationFail("AudioBusConfig: bus '" + idStr + "' must parent to 'master', got '" + parentStr + "'", emitErrors);
                }
            }
            if (!masterSeen) return ValidationFail("AudioBusConfig: master bus missing", emitErrors);
            // Every documented bus must be present so the smoke can assert them by id.
            foreach (object requiredId in AudioEventSeam.ALL_BUS_IDS)
            {
                if (!seenIds.Has(V.Str(requiredId)))
                    return ValidationFail("AudioBusConfig: required bus '" + V.Str(requiredId) + "' missing", emitErrors);
            }
            _validated = true;
            return true;
        }

        /// <summary>Godot <c>typeof()</c> ids for the Variant kinds a bus entry can hold (used only in error text).</summary>
        static long GodotTypeId(object v)
        {
            switch (v)
            {
                case null: return 0;
                case bool _: return 1;
                case long _: return 2;
                case double _: return 3;
                case string _: return 4;
                case Vec2i _: return 6;
                case Vec3 _: return 9;
                case GdDict _: return 27;
                case GdArray _: return 28;
                default: return 24;
            }
        }

        bool ValidationFail(string reason, bool emitErrors)
        {
            if (emitErrors) CoreServices.Log.Error(reason);
            _validated = false;
            return false;
        }

        /// <summary>The bus record (live reference, as in GDScript), or an empty dictionary when missing.</summary>
        public GdDict GetBus(string busId)
        {
            foreach (object bus in Buses)
            {
                if (!(bus is GdDict busDict)) continue;
                if (V.Str(busDict.Get("id", "")) == busId) return busDict;
            }
            return new GdDict();
        }

        /// <summary>The bus volume (dB), or 0.0 when the bus is missing.</summary>
        public double GetVolumeDb(string busId)
        {
            GdDict bus = GetBus(busId);
            if (bus.IsEmpty) return 0.0;
            return V.F64(bus.Get("volume_db", 0.0));
        }

        /// <summary>Sets a bus volume; false when the bus is missing or the value is out of range. Re-validates.</summary>
        public bool SetVolumeDb(string busId, double volumeDb)
        {
            if (volumeDb < MIN_DB || volumeDb > MAX_DB || double.IsNaN(volumeDb) || double.IsInfinity(volumeDb)) return false;
            for (int i = 0; i < Buses.Count; i++)
            {
                if (!(Buses[i] is GdDict busDict)) continue;
                if (V.Str(busDict.Get("id", "")) == busId)
                {
                    busDict["volume_db"] = volumeDb;
                    Buses[i] = busDict;
                    _validated = false;
                    Validate();
                    return true;
                }
            }
            return false;
        }

        public bool SetMuted(string busId, bool muted)
        {
            for (int i = 0; i < Buses.Count; i++)
            {
                if (!(Buses[i] is GdDict busDict)) continue;
                if (V.Str(busDict.Get("id", "")) == busId)
                {
                    busDict["muted"] = muted;
                    Buses[i] = busDict;
                    return true;
                }
            }
            return false;
        }

        public bool IsMuted(string busId)
        {
            GdDict bus = GetBus(busId);
            if (bus.IsEmpty) return false;
            return V.Bool(bus.Get("muted", false));
        }

        /// <summary>Summary dictionary for save/load (REQ-AU-010).</summary>
        public GdDict GetSummary()
        {
            var volumes = new GdDict();
            var mutes = new GdDict();
            foreach (object bus in Buses)
            {
                if (!(bus is GdDict busDict)) continue;
                string idStr = V.Str(busDict.Get("id", ""));
                if (idStr.Length == 0) continue;
                volumes[idStr] = V.F64(busDict.Get("volume_db", 0.0));
                mutes[idStr] = V.Bool(busDict.Get("muted", false));
            }
            return new GdDict
            {
                { "kind", "audio_bus_config" },
                { "volumes", volumes },
                { "mutes", mutes },
            };
        }

        /// <summary>Applies a (possibly partial) summary. Returns true when any set succeeded.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (V.Str(summary.Get("kind", "")) != "audio_bus_config") return false;
            bool changed = false;
            if (summary.Get("volumes") is GdDict volumes)
            {
                foreach (object busId in new List<object>(volumes.Keys))
                {
                    double newVol = V.F64(volumes[busId]);
                    if (SetVolumeDb(V.Str(busId), newVol)) changed = true;
                }
            }
            if (summary.Get("mutes") is GdDict mutes)
            {
                foreach (object busId in new List<object>(mutes.Keys))
                {
                    bool newMuted = V.Bool(mutes[busId]);
                    if (SetMuted(V.Str(busId), newMuted)) changed = true;
                }
            }
            return changed;
        }

        public bool IsValidated() => _validated;
    }
}
