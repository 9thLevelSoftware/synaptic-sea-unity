// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: loot containers (3011-3060, 6080-6119), threat kills
// and corpse loot (6292-6405), the loot context resolvers (11244-11363) and _postprocess_loot_grants (11365-11413).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Scattered loot containers for the active ship (derelict or home); searched ids read as searched.</summary>
        void BuildLootContainers()
        {
            ClearLootContainers();
            if (CurrentShip == null)
                return;
            IShipLoaderView activeLoader = AwayFromStart && CurrentShip != null ? CurrentShip.SceneRoot as IShipLoaderView : Loader;
            if (activeLoader == null || !activeLoader.IsValid)
                return;
            GdArray looted = CurrentShip.LootedContainerIds;
            foreach (object specObj in activeLoader.GetLootContainerSpecsCopy())
            {
                if (!(specObj is GdDict spec))
                    continue;
                string cid = V.Str(spec.Get("id", ""));
                if (cid.Length == 0 || !(spec.Get("position", null) is Vec3 pos))
                    continue;
                var lc = new LootContainer();
                string seedSource = CurrentShip.MarkerId + ":" + cid;
                lc.Configure(cid, V.Str(spec.Get("loot_table", "generic_crate")), seedSource, InventoryState, _loot_tables, pos, 1.8, BuildLootContext(spec), UniqueItemState);
                if (looted.Contains(cid))
                    lc.SetSearched(true);
                LootContainer bound = lc;
                lc.ContainerSearched += (id, granted) => OnLootContainerSearched(id, granted, bound);
                if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                    lc.Parent = CurrentShip.SceneRoot;
                LootContainers.Add(Spawn(lc));
            }
            SpawnPendingCorpseLootContainers();
        }

        void ClearLootContainers()
        {
            foreach (LootContainer lc in LootContainers)
                Despawn(lc);
            LootContainers.Clear();
        }

        /// <summary>Records a searched container on the ship, trains scavenging, postprocesses grants, auto-equips.</summary>
        void OnLootContainerSearched(string containerId, GdArray granted, LootContainer source)
        {
            if (CurrentShip != null && !CurrentShip.LootedContainerIds.Contains(containerId))
                CurrentShip.LootedContainerIds.Add(containerId);
            if (CurrentShip != null && GdString.BeginsWith(containerId, "corpse_"))
                ClearPendingCorpseLoot(CurrentShip, containerId);
            EmitTrainingEvent("scavenge_container", containerId);
            TriggerTutorial("loot_searched", "any");
            TryUnlockAchievement("loot_searched", containerId);
            if (granted.IsEmpty)
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            else if (source != null && source.IsValid)
                PlaySfx(AudioEventSeam.SFX_TOOL_USE, source.GlobalPosition);
            else
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            PostprocessLootGrants(granted, containerId, source);
            RefreshInventoryHud();
            foreach (object entryObj in granted)
            {
                var entry = entryObj as GdDict ?? new GdDict();
                string gid = V.Str(entry.Get("item_id", ""));
                if (gid == "")
                    continue;
                RegisterFoodForSpoilage(gid);
                if (EquipmentState != null && EquipmentState.CanEquip(gid))
                    EquipFromInventory(gid, true);
            }
            RecomputePlayerEncumbrance();
            Log.Info("LOOT CONTAINER SEARCHED marker=" + (CurrentShip != null ? CurrentShip.MarkerId : "") + " container=" + containerId + " granted=" + granted.Count);
        }

        /// <summary>Domain 2 (BP3): a threat died — XP, and a lootable corpse container persisted on the ship.</summary>
        void OnThreatKilled(GdDict record)
        {
            ThreatsKilledCount += 1;
            EmitTrainingEvent("threat_killed", V.Str(record.Get("archetype_id", "")));
            string weaponId = V.Str(record.Get("weapon_id", record.Get("killed_by_weapon", "")));
            if (weaponId.Length == 0 || weaponId == "crowbar" || weaponId == "unarmed")
                EmitTrainingEvent("intimidate_threat", V.Str(record.Get("archetype_id", "")));
            if (InventoryState == null)
                return;
            Vec3 pos = record.Get("position", Vec3.Zero) is Vec3 p ? p : Vec3.Zero;
            string cid = "corpse_" + V.Str(record.Get("instance_id", ""));
            IShipSceneRoot parent = null;
            Vec3 localPos = pos;
            if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
            {
                parent = CurrentShip.SceneRoot;
                localPos = ToLocal(CurrentShip.SceneRoot, pos);
            }
            string lootTable = V.Str(record.Get("loot_table", "combat_drop_common"));
            string seedSource = "kill:" + cid;
            if (CurrentShip != null)
                RegisterPendingCorpseLoot(CurrentShip, cid, lootTable, seedSource, localPos);
            SpawnCorpseLootContainer(cid, lootTable, seedSource, localPos, parent);
        }

        static void RegisterPendingCorpseLoot(ShipInstance ship, string containerId, string lootTable, string seedSource, Vec3 localPos)
        {
            if (ship == null || containerId.Length == 0)
                return;
            foreach (object entry in ship.PendingCorpseLoot)
            {
                if (entry is GdDict e && V.Str(e.Get("container_id", "")) == containerId)
                    return;
            }
            ship.PendingCorpseLoot.Add(new GdDict
            {
                { "container_id", containerId },
                { "loot_table", lootTable },
                { "seed_source", seedSource },
                { "position", GdArray.Of((double)localPos.X, (double)localPos.Y, (double)localPos.Z) },
            });
        }

        static void ClearPendingCorpseLoot(ShipInstance ship, string containerId)
        {
            if (ship == null || containerId.Length == 0)
                return;
            var kept = new GdArray();
            foreach (object entry in ship.PendingCorpseLoot)
            {
                if (!(entry is GdDict e))
                    continue;
                if (V.Str(e.Get("container_id", "")) == containerId)
                    continue;
                kept.Add(e);
            }
            ship.PendingCorpseLoot = kept;
        }

        void SpawnCorpseLootContainer(string containerId, string lootTable, string seedSource, Vec3 localPos, IShipSceneRoot parent)
        {
            if (InventoryState == null)
                return;
            foreach (LootContainer existing in LootContainers)
            {
                if (existing.IsValid && existing.ContainerId == containerId)
                    return;
            }
            var lc = new LootContainer();
            lc.Configure(containerId, lootTable, seedSource, InventoryState, _loot_tables, localPos, 1.8, new GdDict());
            LootContainer bound = lc;
            lc.ContainerSearched += (id, granted) => OnLootContainerSearched(id, granted, bound);
            lc.Parent = parent;
            LootContainers.Add(Spawn(lc));
        }

        void SpawnPendingCorpseLootContainers()
        {
            if (CurrentShip == null || InventoryState == null)
                return;
            GdArray looted = CurrentShip.LootedContainerIds;
            IShipSceneRoot parent = AwayFromStart && RootValid(CurrentShip.SceneRoot) ? CurrentShip.SceneRoot : null;
            foreach (object entryObj in new GdArray(CurrentShip.PendingCorpseLoot))
            {
                if (!(entryObj is GdDict entry))
                    continue;
                string cid = V.Str(entry.Get("container_id", ""));
                if (cid.Length == 0 || looted.Contains(cid))
                    continue;
                Vec3 localPos = Vec3.Zero;
                if (entry.Get("position", null) is GdArray posArr && posArr.Count == 3)
                    localPos = new Vec3(V.F64(posArr[0]), V.F64(posArr[1]), V.F64(posArr[2]));
                SpawnCorpseLootContainer(cid, V.Str(entry.Get("loot_table", "combat_drop_common")), V.Str(entry.Get("seed_source", "kill:" + cid)), localPos, parent);
            }
        }

        /// <summary>Search a container through its real interaction (was <c>search_loot_container_for_validation</c>).</summary>
        public bool SearchLootContainer(string containerId)
        {
            foreach (LootContainer lc in LootContainers)
            {
                if (lc.IsValid && lc.ContainerId == containerId && !lc.Searched)
                {
                    lc.SetValidationPlayerInRange(true);
                    return lc.TryInteract(PlayerPos);
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ loot context
        GdDict BuildLootContext(GdDict spec)
        {
            var ctx = new GdDict
            {
                { "biome_id", ResolveCurrentLootBiomeId() },
                { "loot_quality_modifier", ResolveCurrentLootQualityModifier() },
                { "depth", ResolveCurrentLootDepth() },
                { "condition", ResolveCurrentLootCondition() },
                { "container_kind", V.Str(spec.Get("kind", spec.Get("loot_table", "generic_crate"))) },
                { "item_definitions", ItemDefs.LoadDefinitions() },
            };
            // RUNTIME note: "unique_state" (the UniqueItemState object) travels separately (LootContainer.UniqueState).
            if (spec.Has("contents") && spec.Get("contents", null) is GdArray)
                ctx["contents"] = LootContainer.NormalizedContents(spec);
            return ctx;
        }

        double ResolveCurrentLootQualityModifier()
        {
            double quality = ResolveCurrentLootBiomeQualityModifier();
            // Unity port (C4): the run difficulty's loot dial on the home ship (exactly 1.0 for "standard").
            double difficultyMult = HomeDifficultyLootMultiplier();
            if (difficultyMult != 1.0)
                quality = GdMath.Clampf(quality * difficultyMult, DifficultyProfile.COMBINED_MODIFIER_MIN, DifficultyProfile.COMBINED_MODIFIER_MAX);
            return quality;
        }

        double ResolveCurrentLootBiomeQualityModifier()
        {
            string biomeId = ResolveCurrentLootBiomeId();
            if (biomeId.Length == 0)
                return 1.0;
            string relPath = "res://data/procgen/biomes/" + biomeId + ".json";
            if (CatalogRegistry.Exists(relPath))
            {
                GdDict parsed = CatalogRegistry.LoadDict(relPath);
                if (parsed != null)
                {
                    BiomeProfile biome = BiomeProfile.FromDict(parsed);
                    if (biome != null)
                        return biome.LootQualityModifier;
                }
            }
            switch (biomeId)
            {
                case "breach_field": return 1.1;
                case "dead_fleet": return 1.4;
                default: return 1.0;
            }
        }

        string ResolveCurrentLootBiomeId()
        {
            // Unity port (C4): an explicit run biome skins / loots the home ship (and the lifeboat built there).
            if (BiomeId.Length > 0 && !AwayFromStart)
                return BiomeId;
            List<string> biomeIds = LootBiomeIds();
            if (biomeIds.Count == 0)
                return "abyssal_synaptic_sea";
            long seedValue = 0;
            if (CurrentShip != null && CurrentShip.Blueprint != null)
                seedValue = CurrentShip.Blueprint.SeedValue;
            return BiomeProfile.SelectBiome(seedValue, biomeIds);
        }

        long ResolveCurrentLootDepth()
        {
            if (CurrentShip == null || CurrentShip.Blueprint == null)
                return 0;
            ShipBlueprint bp = CurrentShip.Blueprint;
            return Math.Max(0, bp.ShipSize * 2 + bp.ShipCondition + (AwayFromStart ? 1 : 0));
        }

        string ResolveCurrentLootCondition()
        {
            if (CurrentShip == null || CurrentShip.Blueprint == null)
                return "damaged";
            switch (CurrentShip.Blueprint.ShipCondition)
            {
                case 0: return "pristine";
                case 1: return "damaged";
                default: return "wrecked";
            }
        }

        List<string> LootBiomeIds()
        {
            if (_lootBiomeIdsCache.Count > 0)
                return _lootBiomeIdsCache;
            GdDict parsed = CatalogRegistry.LoadDict("res://data/items/biome_definitions.json");
            if (parsed != null && parsed.Get("biomes", null) is GdDict biomes)
            {
                foreach (object biomeId in biomes.Keys)
                    _lootBiomeIdsCache.Add(V.Str(biomeId));
            }
            GdString.SortStrings(_lootBiomeIdsCache);
            if (_lootBiomeIdsCache.Count == 0)
                _lootBiomeIdsCache.Add("abyssal_synaptic_sea");
            return _lootBiomeIdsCache;
        }

        /// <summary>Unique claims, codex unlocks, auto-equip, web-chart import, and the HUD loot feedback line.</summary>
        void PostprocessLootGrants(GdArray granted, string sourceId, SessionInteractable source)
        {
            if (granted.IsEmpty)
            {
                _lastLootFeedbackLine = "Loot: " + sourceId + " empty";
                return;
            }
            foreach (object entryObj in granted)
            {
                if (!(entryObj is GdDict entry))
                    continue;
                string itemId = V.Str(entry.Get("item_id", ""));
                string uniqueId = V.Str(entry.Get("unique_id", ""));
                string seedKey = V.Str(entry.Get("seed_key", ""));
                string codexEntryId = V.Str(entry.Get("codex_entry_id", ""));
                if (UniqueItemState != null && uniqueId.Length > 0)
                    UniqueItemState.Claim(uniqueId, seedKey, codexEntryId);
                else if (UniqueItemState != null && codexEntryId.Length > 0)
                    UniqueItemState.RecordCodexUnlock(codexEntryId);
                if (MetaProgressionState != null && codexEntryId.Length > 0)
                    MetaProgressionState.UnlockCodexEntry(codexEntryId);
                if (EquipmentState != null && itemId != "" && EquipmentState.CanEquip(itemId))
                    EquipFromInventory(itemId, true);
                if (itemId == "web_chart" && SynapticSeaWorld != null)
                {
                    double scanRange = ScannerState != null ? ScannerState.RangeRadius : 250.0;
                    var importViews = new GdArray();
                    foreach (ShipMarker m in SynapticSeaWorld.MarkersInRange(scanRange))
                    {
                        importViews.Add(new GdDict
                        {
                            { "marker_id", m.MarkerId },
                            { "position", GdArray.Of((double)m.Position.X, (double)m.Position.Y, (double)m.Position.Z) },
                            { "distance", (double)m.Position.DistanceTo(SynapticSeaWorld.PlayerPosition) },
                            { "size_class", m.SizeClass },
                            { "ship_type", m.ShipType },
                        });
                    }
                    WebChartState.RecordViews(importViews, 2);
                }
            }
            var first = (GdDict)granted[0];
            string rarityText = RarityTier.Label(V.Str(first.Get("rarity", "common")));
            _lastLootFeedbackLine = "Loot: " + ItemDefs.DisplayName(ItemDefs.LoadDefinitions(), V.Str(first.Get("item_id", "")))
                                    + " x" + V.I64(first.Get("quantity", 0L)) + " [" + rarityText + "]";
            if (source != null && source.IsValid && source.IsInsideTree)
                PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP, source.GlobalPosition);
            else
                PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
        }
    }
}
