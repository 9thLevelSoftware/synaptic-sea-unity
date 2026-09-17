// Ported from scripts/systems/save_migration_service.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Save migration service (ADR-0032). Deterministic migration table mapping from_version -> to_version.
    /// Each step receives the parsed dictionary and returns a new dictionary with the target version's keys.
    /// New steps are appended; old steps are never removed.
    /// </summary>
    public class SaveMigrationService
    {
        /// <summary>The ordered list of run slot schema versions the service knows how to walk.</summary>
        public static readonly GdArray KnownVersions = GdArray.Of(
            "gate2-current-run-1",  // legacy: 6 model summaries, no player_progression
            "gate2-current-run-2",  // added player_progression_summary (Phase 3)
            "gate2-current-run-3",  // added slot_id / slot_kind / parent_world_slot metadata (Task 11)
            "gate2-current-run-4",  // added play_time_seconds / current_location / world_seed (ADR-0046)
            "gate2-current-run-5"   // Unity port: wounds / web chart / tutorial / equipment / home loot+inventory / run_context
        );

        /// <summary>The last run schema Godot 96ecb2b0 wrote; run-5 is a Unity-port superset of it.</summary>
        public const string GodotTargetVersion = "gate2-current-run-4";

        public const string TargetVersion = "gate2-current-run-5";
        public const string WorldTargetVersion = "world-4";

        /// <summary>Returns <c>{dict, from_version, to_version, migrated}</c>; <c>dict</c> is null when rejected.</summary>
        public GdDict MigrateRun(object parsed)
        {
            if (!(parsed is GdDict dict))
                return new GdDict { { "dict", null }, { "from_version", "" }, { "to_version", TargetVersion }, { "migrated", false } };
            string current = V.Str(dict.Get("slice_version", ""));
            if (current == TargetVersion)
                return new GdDict { { "dict", dict }, { "from_version", current }, { "to_version", TargetVersion }, { "migrated", false } };
            if (current.Length == 0)
            {
                // Legacy file without slice_version: treat as the oldest known version.
                current = V.Str(KnownVersions[0]);
            }
            if (IndexOf(current) < 0)
            {
                // Newer than us: cannot downgrade.
                return new GdDict { { "dict", null }, { "from_version", current }, { "to_version", TargetVersion }, { "migrated", false } };
            }
            GdDict working = dict.DeepCopy();
            bool migrated = false;
            // Walk the chain by index so a step that forgets to bump the version cannot loop forever.
            GdArray chain = KnownVersions.ShallowCopy();
            int startIdx = IndexOf(current);
            int targetIdx = IndexOf(TargetVersion);
            while (startIdx < targetIdx)
            {
                string fromV = V.Str(chain[startIdx]);
                Func<GdDict, GdDict> step = Step(fromV);
                if (step == null) break;
                working = step(working);
                // Stamp the next known version so the file is forward-compatible.
                string nextV = startIdx + 1 < chain.Count ? V.Str(chain[startIdx + 1]) : TargetVersion;
                working["slice_version"] = nextV;
                startIdx += 1;
                migrated = true;
            }
            working["slice_version"] = TargetVersion;
            return new GdDict { { "dict", working }, { "from_version", current }, { "to_version", TargetVersion }, { "migrated", migrated } };
        }

        public GdDict MigrateWorld(object parsed)
        {
            if (!(parsed is GdDict dict))
                return new GdDict { { "dict", null }, { "from_version", "" }, { "to_version", WorldTargetVersion }, { "migrated", false } };
            string current = V.Str(dict.Get("slice_version", ""));
            if (current == WorldTargetVersion)
            {
                GdDict currentInner = MigrateWorldHomeShip(dict);
                return new GdDict
                {
                    { "dict", currentInner["dict"] },
                    { "from_version", current },
                    { "to_version", WorldTargetVersion },
                    { "migrated", V.Bool(currentInner["migrated"]) },
                };
            }
            if (current.Length == 0) current = "world-1"; // legacy world snapshot
            // Unknown versions have no step: pass through untouched so a NEWER world can be rejected by the load
            // path without being quarantined as corrupt (Session 3 audit is_valid() guard).
            Func<GdDict, GdDict> step = WorldStep(current);
            if (step == null)
            {
                return new GdDict
                {
                    { "dict", dict },
                    { "from_version", current },
                    { "to_version", WorldTargetVersion },
                    { "migrated", false },
                    { "newer_than_current", IsNewerWorldVersion(current) },
                };
            }
            GdDict working = step(dict);
            working["slice_version"] = WorldTargetVersion;
            return new GdDict { { "dict", working }, { "from_version", current }, { "to_version", WorldTargetVersion }, { "migrated", true } };
        }

        Func<GdDict, GdDict> Step(string fromVersion)
        {
            switch (fromVersion)
            {
                case "gate2-current-run-1": return MigrateV1ToV2;
                case "gate2-current-run-2": return MigrateV2ToV3;
                case "gate2-current-run-3": return MigrateV3ToV4;
                case "gate2-current-run-4": return MigrateV4ToV5;
            }
            return null;
        }

        Func<GdDict, GdDict> WorldStep(string fromVersion)
        {
            switch (fromVersion)
            {
                case "world-1":
                case "world-2":
                case "world-3":
                    return MigrateWorldLegacyToWorld4;
            }
            return null;
        }

        static int IndexOf(string version) => KnownVersions.IndexOf(version);

        bool IsNewerWorldVersion(string version)
        {
            long currentNum = WorldVersionNumber(version);
            long targetNum = WorldVersionNumber(WorldTargetVersion);
            return currentNum > targetNum && targetNum >= 0;
        }

        long WorldVersionNumber(string version)
        {
            const string prefix = "world-";
            if (!version.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            string suffix = version.Substring(prefix.Length);
            if (suffix.Length == 0 || !InfraCompat.IsValidInt(suffix)) return -1;
            return V.I64(suffix);
        }

        GdDict MigrateV1ToV2(GdDict dict)
        {
            // Add player_progression_summary default if missing or an empty {} placeholder.
            GdDict output = dict.DeepCopy();
            object existingPp = output.Get("player_progression_summary", null);
            if (existingPp == null || (existingPp is GdDict pp && pp.IsEmpty))
            {
                output["player_progression_summary"] = new GdDict
                {
                    { "class_id", "" },
                    { "xp", new GdDict() },
                    { "level", 1L },
                };
            }
            return output;
        }

        GdDict MigrateV2ToV3(GdDict dict)
        {
            // Add slot identity defaults so a migrated legacy save renders in the menu.
            GdDict output = dict.DeepCopy();
            if (!output.Has("slot_id")) output["slot_id"] = "";
            if (!output.Has("slot_kind")) output["slot_kind"] = "";
            if (!output.Has("is_autosave")) output["is_autosave"] = false;
            if (!output.Has("is_quicksave")) output["is_quicksave"] = false;
            if (!output.Has("parent_world_slot")) output["parent_world_slot"] = "";
            return output;
        }

        GdDict MigrateV3ToV4(GdDict dict)
        {
            // ADR-0046: real slot metadata; default honest zeros for older saves.
            GdDict output = dict.DeepCopy();
            if (!output.Has("play_time_seconds")) output["play_time_seconds"] = 0.0;
            if (!output.Has("current_location")) output["current_location"] = "";
            if (!output.Has("world_seed")) output["world_seed"] = 0L;
            return output;
        }

        /// <summary>
        /// Unity port: the gate2-current-run-5 keys default to empty containers, which the session reads as "not saved"
        /// (fresh wounds and tutorial state, empty chart, keep live equipment / loot / cargo, keep the run context).
        /// </summary>
        public static readonly GdDict V5Defaults = new GdDict
        {
            { "wound_summary", new GdDict() },
            { "web_chart_summary", new GdDict() },
            { "tutorial_summary", new GdDict() },
            { "equipment_summary", new GdDict() },
            { "home_looted_containers", new GdArray() },
            { "home_ship_inventory", new GdDict() },
            { "run_context", new GdDict() },
        };

        GdDict MigrateV4ToV5(GdDict dict)
        {
            GdDict output = dict.DeepCopy();
            foreach (object key in V5Defaults.Keys)
            {
                if (!output.Has(key)) output[key] = V.DeepCopy(V5Defaults[key]);
            }
            return output;
        }

        GdDict MigrateWorldLegacyToWorld4(GdDict dict)
        {
            // The embedded home_ship is a full RunSnapshot dict with its own slice_version; migrate it too.
            return (GdDict)MigrateWorldHomeShip(dict)["dict"];
        }

        GdDict MigrateWorldHomeShip(GdDict dict)
        {
            GdDict output = dict.DeepCopy();
            bool migrated = false;
            object homeShip = output.Get("home_ship", null);
            if (homeShip is GdDict homeShipDict && !homeShipDict.IsEmpty)
            {
                GdDict inner = MigrateRun(homeShipDict);
                object innerDict = inner.Get("dict", null);
                if (innerDict is GdDict innerD)
                {
                    output["home_ship"] = innerD;
                    migrated = V.Bool(inner.Get("migrated", false))
                        || V.Str(homeShipDict.Get("slice_version", "")) != V.Str(innerD.Get("slice_version", ""));
                }
                // A null inner result (newer-than-us home_ship inside a LEGACY world file) keeps the original dict;
                // RunSnapshot.FromDict then rejects it.
            }
            return new GdDict { { "dict", output }, { "migrated", migrated } };
        }
    }
}
