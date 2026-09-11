// Ported from scripts/systems/permadeath_resolver.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed <c>has_died_in(slot_id)</c> seam <see cref="TitleSaveQuery"/> calls on its resolver.
    /// </summary>
    public interface IDeathRecordQuery
    {
        bool HasDiedIn(string slotId);
    }

    /// <summary>
    /// Permadeath / epitaph flow (ADR-0032). Pure model plus the per-slot death record file
    /// (<c>user://saves/&lt;slot_id&gt;.death.json</c>), read and written through an injected <see cref="IStorage"/>.
    /// </summary>
    public class PermadeathResolver : IDeathRecordQuery
    {
        public const string DeathKindSuffix = ".death.json";

        IStorage _storage;
        IClock _clock;

        public PermadeathResolver(IStorage storage = null, IClock clock = null)
        {
            _storage = storage;
            _clock = clock;
        }

        /// <summary><c>user://</c> backing store; defaults to <see cref="CoreServices.UserStorage"/>.</summary>
        public IStorage Storage
        {
            get => _storage ?? CoreServices.UserStorage;
            set => _storage = value;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        public string DeathPathFor(string slotId)
        {
            // The death record sits next to the slot file; it is not embedded because the slot payload is the
            // RunSnapshot schema and mixing them would break the migration chain.
            return "user://saves/" + slotId + DeathKindSuffix;
        }

        public bool HasDiedIn(string slotId) => Storage.FileExists(DeathPathFor(slotId));

        public GdDict LoadEpitaph(string slotId)
        {
            string path = DeathPathFor(slotId);
            if (!Storage.FileExists(path)) return new GdDict();
            string json = Storage.ReadText(path);
            if (json == null) return new GdDict();
            object parsed = GdJson.ParseString(json);
            if (!(parsed is GdDict dict)) return new GdDict();
            return dict;
        }

        public GdDict RecordDeath(string slotId, string cause, string epitaph, double runTimeSeconds, long finalObjectiveSequence)
        {
            var record = new GdDict
            {
                { "slot_id", slotId },
                { "cause", cause },
                { "epitaph", epitaph },
                { "died_at", Clock.DateTimeString(true) },
                { "died_at_epoch", GdMath.Trunc(Clock.UnixTime()) },
                { "run_time_seconds", runTimeSeconds },
                { "final_objective_sequence", finalObjectiveSequence },
                { "schema_version", "death-1" },
            };
            const string dirPath = "user://saves";
            if (!Storage.DirExists(dirPath))
            {
                try
                {
                    Storage.MakeDirRecursive(dirPath);
                }
                catch (Exception e)
                {
                    CoreServices.Log.Warning("PermadeathResolver: failed to create saves dir, error=" + e.Message);
                    return new GdDict();
                }
            }
            try
            {
                Storage.WriteText(DeathPathFor(slotId), GdJson.Stringify(record, "\t"));
            }
            catch (Exception e)
            {
                CoreServices.Log.Warning("PermadeathResolver: cannot open death file for writing, error=" + e.Message);
                return new GdDict();
            }
            return record;
        }

        public bool ClearDeath(string slotId)
        {
            if (!HasDiedIn(slotId)) return true;
            return Storage.Delete(DeathPathFor(slotId));
        }
    }
}
