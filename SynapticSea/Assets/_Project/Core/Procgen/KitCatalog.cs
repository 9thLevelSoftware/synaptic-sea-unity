// Ported from scripts/procgen/kit_catalog.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Registry of ship structural kits indexed by kit_id and role. Kits are JSON files under
    /// <c>res://data/kits/</c>; <see cref="Configure"/> parses every <c>*.json</c> and builds, per kit:
    /// <c>{kit_id, description, modules, role_modules, default_role_module, biome_preference}</c>.
    /// With no kit registered, <see cref="KitsForRole"/> falls back to <see cref="FALLBACK_MODULES"/>.
    /// </summary>
    public sealed class KitCatalog
    {
        public static readonly IReadOnlyList<string> FALLBACK_MODULES = new[] { "floor_1x1" };

        /// <summary>Default kit id consulted when callers don't specify one.</summary>
        public const string DEFAULT_KIT_ID = "ship_structural_v0";

        // kit_id -> kit record (insertion order = directory walk order; see ProcgenCompat.ListResFiles).
        GdDict _kits = new GdDict();
        string _defaultKitId = "";

        static readonly string[] RolesNeedingDefault =
        {
            "airlock", "corridor", "engineering", "life_support", "bridge",
            "cargo", "crew_quarters", "medical", "maintenance",
            "cockpit", "engine_bay", "compartment", "bay", "quarters",
            "dock", "reactor", "main_spine", "hub", "ramp", "elevator",
            "storage", "mess_hall", "armory", "hangar",
        };

        /// <summary>
        /// Loads every <c>*.json</c> under <paramref name="kitsDir"/> and returns the number of kits loaded.
        /// Malformed kit files are warned about and skipped. Idempotent.
        /// </summary>
        public long Configure(string kitsDir = "res://data/kits/")
        {
            _kits = new GdDict();
            if (!ProcgenCompat.ResDirExists(kitsDir))
            {
                CoreServices.Log.Warning("KIT CATALOG FAIL kits_dir not found: " + kitsDir);
                _defaultKitId = "";
                return 0;
            }

            long loaded = 0;
            foreach (string entry in ProcgenCompat.ListResFiles(kitsDir))
            {
                if (!entry.EndsWith(".json", System.StringComparison.Ordinal)) continue;
                string fullPath = ProcgenCompat.PathJoin(kitsDir, entry);
                GdDict kit = LoadKitFile(fullPath);
                if (kit.IsEmpty) continue;
                string kid = V.Str(kit.Get("kit_id", ""));
                if (kid.Length != 0 && !_kits.Has(kid))
                {
                    _kits[kid] = kit;
                    loaded += 1;
                }
            }

            // Pick a default kit id.
            if (_kits.Has(DEFAULT_KIT_ID)) _defaultKitId = DEFAULT_KIT_ID;
            else if (_kits.Count > 0) _defaultKitId = V.Str(_kits.Keys[0]);
            else _defaultKitId = "";

            return loaded;
        }

        /// <summary>
        /// Module list for <paramref name="role"/> from the default kit, or from the kit with the highest
        /// biome_preference for <paramref name="biome"/>. Falls back to the kit default module, then
        /// <see cref="FALLBACK_MODULES"/>.
        /// </summary>
        public List<string> KitsForRole(string role, string biome = "")
        {
            if (_kits.IsEmpty) return new List<string>(FALLBACK_MODULES);

            string kitId = _defaultKitId;
            if (!string.IsNullOrEmpty(biome))
            {
                string candidate = BestKitForBiome(biome);
                if (candidate.Length != 0) kitId = candidate;
            }

            if (!_kits.Has(kitId)) return new List<string>(FALLBACK_MODULES);

            var kit = (GdDict)_kits[kitId];
            GdDict roleModules = kit.GetDictOrEmpty("role_modules");
            if (roleModules.Has(role))
            {
                if (roleModules[role] is GdArray raw)
                {
                    var typed = new List<string>();
                    foreach (var entry in raw) typed.Add(V.Str(entry));
                    if (typed.Count > 0) return typed;
                }
            }

            string defaultModule = V.Str(kit.Get("default_role_module", ""));
            if (defaultModule.Length != 0) return new List<string> { defaultModule };
            return new List<string>(FALLBACK_MODULES);
        }

        /// <summary>
        /// True iff the kit selected for <paramref name="biome"/> (default kit when empty) defines
        /// <paramref name="role"/> explicitly in its role_modules map.
        /// </summary>
        public bool HasRoleFor(string role, string biome = "")
        {
            if (_kits.IsEmpty) return false;
            string kitId = _defaultKitId;
            if (!string.IsNullOrEmpty(biome))
            {
                string candidate = BestKitForBiome(biome);
                if (candidate.Length != 0) kitId = candidate;
            }
            if (!_kits.Has(kitId)) return false;
            var kit = (GdDict)_kits[kitId];
            GdDict roleModules = kit.GetDictOrEmpty("role_modules");
            return roleModules.Has(role);
        }

        /// <summary>A single module id for <paramref name="role"/> from <paramref name="kitId"/>.</summary>
        public string ModuleIdForRole(string kitId, string role)
        {
            if (!_kits.Has(kitId)) return "floor_1x1";
            var kit = (GdDict)_kits[kitId];
            GdDict roleModules = kit.GetDictOrEmpty("role_modules");
            if (roleModules.Has(role))
            {
                if (roleModules[role] is GdArray raw && !raw.IsEmpty) return V.Str(raw[0]);
            }
            return V.Str(kit.Get("default_role_module", "floor_1x1"));
        }

        public bool IsLoaded(string kitId) => _kits.Has(kitId);

        /// <summary>Registered kit ids, sorted.</summary>
        public List<string> LoadedKitIds()
        {
            var ids = new List<object>(_kits.Keys);
            GdSort.Sort(ids);
            var typed = new List<string>();
            foreach (var entry in ids) typed.Add(V.Str(entry));
            return typed;
        }

        public string DefaultKitId() => _defaultKitId;

        // --- Internal helpers ---

        GdDict LoadKitFile(string path)
        {
            if (!CatalogRegistry.Exists(path)) return new GdDict();
            GdDict data = CatalogRegistry.LoadDict(path);
            if (data == null)
            {
                CoreServices.Log.Warning("KIT CATALOG FAIL invalid JSON: " + path);
                return new GdDict();
            }
            string kitId = V.Str(data.Get("kit_id", ""));
            if (kitId.Length == 0)
            {
                CoreServices.Log.Warning("KIT CATALOG FAIL missing kit_id: " + path);
                return new GdDict();
            }

            var roleModules = new GdDict();
            if (data.Get("role_modules", new GdDict()) is GdDict roleModulesRaw)
            {
                foreach (var kv in roleModulesRaw)
                {
                    if (kv.Value is GdArray v)
                    {
                        var typed = new GdArray();
                        foreach (var entry in v) typed.Append(V.Str(entry));
                        if (!typed.IsEmpty) roleModules[V.Str(kv.Key)] = typed;
                    }
                }
            }

            // `modules` may be a flat list of module-id strings (legacy kits) or an array of module-catalog
            // objects (asset-manifest schema); for the object form each entry's `module_id` is read.
            var legacyModules = new GdArray();
            if (data.Get("modules", new GdArray()) is GdArray rawModules)
            {
                foreach (var entry in rawModules)
                {
                    if (entry is GdDict entryDict)
                    {
                        string mid = V.Str(entryDict.Get("module_id", ""));
                        if (mid.Length != 0) legacyModules.Append(mid);
                    }
                    else
                    {
                        legacyModules.Append(V.Str(entry));
                    }
                }
            }
            // Honour an explicit `default_role_module`; fall back to the first module only when it is absent.
            string defaultModule = V.Str(data.Get("default_role_module", ""));
            if (defaultModule.Length == 0)
            {
                defaultModule = !legacyModules.IsEmpty ? V.Str(legacyModules[0]) : "floor_1x1";
            }

            // Legacy fall-back: no role_modules field -> uniform map for the standard role set.
            if (roleModules.IsEmpty && !legacyModules.IsEmpty)
            {
                var defaultList = GdArray.Of(defaultModule);
                foreach (string role in RolesNeedingDefault) roleModules[role] = defaultList.ShallowCopy();
            }

            var biomePref = new GdDict();
            if (data.Get("biome_preference", new GdDict()) is GdDict biomePrefRaw)
            {
                foreach (var kv in biomePrefRaw) biomePref[V.Str(kv.Key)] = V.F64(kv.Value);
            }

            return new GdDict
            {
                { "kit_id", kitId },
                { "description", V.Str(data.Get("description", "")) },
                { "modules", legacyModules },
                { "role_modules", roleModules },
                { "default_role_module", defaultModule },
                { "biome_preference", biomePref },
            };
        }

        /// <summary>
        /// The kit with the highest biome_preference for <paramref name="biomeId"/> (first wins on ties, in load
        /// order); the default kit when none declares an affinity; "" only when no kits are loaded.
        /// </summary>
        string BestKitForBiome(string biomeId)
        {
            if (_kits.IsEmpty) return "";
            string bestKit = _defaultKitId;
            double bestScore = double.NegativeInfinity;
            foreach (var kitIdVariant in _kits.Keys)
            {
                string kitId = V.Str(kitIdVariant);
                var kit = (GdDict)_kits[kitId];
                GdDict pref = kit.GetDictOrEmpty("biome_preference");
                if (!pref.Has(biomeId)) continue;
                double score = V.F64(pref[biomeId]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestKit = kitId;
                }
            }
            return bestKit;
        }
    }
}
