using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Explicit new-content profile. No ordinary session activation or survival-policy override.</summary>
    public static class FirstAwaySalvageProfile
    {
        public const string Provider = "managed_first_away_salvage_v1";
        // V1 resources are pinned. Catalog changes require a reviewed profile version, never a silent regeneration.
        static readonly Dictionary<string,string> Pinned = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            { "res://data/procgen/slice/first_run_contract.json", "fbc7966e96a2a41723b3934e16e6dbdbdcca7d2919974ccf58eb1f5c63a2fe57" },
            { "res://data/procgen/biomes/breach_field.json", "b9a352f46a503e5ff89391a9706010301edf0c77328727ce76eb34cda39c0587" },
            { "res://data/procgen/difficulty/standard.json", "0455e5f427044a6968b46b231233f4caec1165eced2320493d1b9857ff9e2136" },
            { "res://data/procgen/encounter_tables/biomatter_lurker.json", "bb42af4232bc6b9566de387263caaff1794730e32da867e3f5b24a3c4d664ed0" },
            { "res://data/items/loot_tables.json", "7fa2f06054e3400421c76c0889942a3ca84ced76e9863244e10bfdea5823583e" },
            { "res://data/kits/ship_structural_hazard.json", "8cdb3fa3dfadcb0dcc92d592dc7ed7af81e1dc545070ef23846e930376bd0731" },
            { "res://data/kits/ship_structural_v0.json", "adde67f7b28d097830662e54475e9ca412ee35a4d459912790b741ea80a43b95" },
            { "res://data/combat/threat_archetypes.json", "e23b29a2715c12f0b1a506816a28082323ffbb777b9cd9f9569596399f9817fd" },
            { "res://data/items/item_definitions.json", "f2248ce62ff716a602665ee39ab3b951c64b64231330b42eff5e1da3987a3780" },
        };
        public static Dictionary<string,FirstAwayCatalogIdentity> LoadPinnedCatalogs()
        {
            var result = new Dictionary<string,FirstAwayCatalogIdentity>(StringComparer.Ordinal);
            foreach (var row in Pinned)
            {
                string text = CoreServices.Resources?.ReadText(row.Key);
                if (text == null || FirstAwayGenerationDescriptor.Hash(FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(text)) != row.Value) return null;
                // Actual stages read CatalogRegistry; refuse stale/tampered parsed authority even if raw files match.
                var parsed = GdJson.ParseDict(text); var cached = CatalogRegistry.LoadDict(row.Key);
                if (parsed == null || cached == null || !V.VariantEquals(parsed,cached)) return null;
                result.Add(row.Key, new FirstAwayCatalogIdentity(FirstAwayGenerationInputs.Profile, row.Value));
            }
            // Pin the semantic effect consumed by both the ordinary contract and runtime hazard coordinator.
            var requiredEffect = new GdDict { { "sim", new GdDict { { "loot_bias", "salvage_cargo" },
                { "hazard", new GdDict { { "kind", "breach" }, { "weight", .6 } } } } }, { "dressing", "vacuum" } };
            if (!V.VariantEquals(new RoomVariantSelector().EffectsFor("breached"), requiredEffect)) return null;
            result.Add("managed:room_variant/breached/v1",new FirstAwayCatalogIdentity(FirstAwayGenerationInputs.Profile,
                FirstAwayGenerationDescriptor.Hash(FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(GdJson.Stringify(requiredEffect)))));
            return result;
        }
        internal static void StampAuthoring(GdDict layout, GdDict grid, FirstAwayGenerationInputs inputs)
        {
            layout["generation_profile"] = FirstAwayGenerationInputs.Profile;
            layout["template_id"] = FirstAwayGenerationInputs.Profile;
            layout["seed_value"] = inputs.CandidateSeed;
            layout["biome_id"] = inputs.Biome; layout["difficulty_id"] = inputs.Difficulty;
            layout["kit_id"] = ShipLayoutGenerator.KitIdForBiome(inputs.Biome);
            layout["hazard_source"] = "runtime";
            layout["first_away_inputs"] = inputs.ToDict();
            layout["first_away_choices"] = grid.GetDictOrEmpty("prototype_choices").DeepCopy();
            layout["first_away_reservation_owner"] = grid.GetString("reservation_owner");
            var reservations = new GdDict();
            foreach (var row in grid.GetDictOrEmpty("reservations"))
            { var cell = (Vec2i)row.Value; reservations[row.Key] = GdArray.Of((long)cell.X, (long)cell.Y, 0L); }
            layout["first_away_reservations"] = reservations;
            foreach (GdDict room in layout.GetArrayOrEmpty("rooms"))
            {
                var authored = grid.GetDictOrEmpty("rooms").GetDictOrEmpty(room.GetString("id"));
                room["semantic_id"] = authored.GetString("semantic_id"); room["owner_id"] = inputs.OwnerId;
            }
            // The existing dock contract uses the first real dock seam; make the outward normal match this transform.
            var dock = layout.GetArrayOrEmpty("rooms").Cast<GdDict>().First();
            var seam = layout.GetArrayOrEmpty("portals").Cast<GdDict>().First(p => p.GetString("from_room") == dock.GetString("id"));
            var a = LayoutSerializer.ParseSlotCell(seam.Get("from_cell")); var b = LayoutSerializer.ParseSlotCell(seam.Get("to_cell"));
            long nx = V.I64(a[0])-V.I64(b[0]), nz = V.I64(a[1])-V.I64(b[1]);
            var chosen = dock.GetArrayOrEmpty("cells").Cast<Vec2i>().OrderByDescending(c => c.X*nx+c.Y*nz).First();
            layout["docking_port"] = new GdDict { { "contract_version", 1L }, { "room_id", dock.GetString("id") },
                { "cell", GdArray.Of((long)chosen.X,(long)chosen.Y) }, { "position", GdArray.Of(chosen.X*4.0,0.0,chosen.Y*4.0) }, { "facing", GdArray.Of((double)nx,0.0,(double)nz) } };
        }
        /// <summary>Hard rejection before stock damage. Never filters the stock pool or accepts saved damage as fresh.</summary>
        public static bool UniqueFreshDamagePool(GdDict layout)
        {
            if (layout == null || layout.GetBool("wreck_applied") || !layout.GetArrayOrEmpty("module_damage").IsEmpty) return false;
            var plan = layout.GetDictOrEmpty("structural_plan"); var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string layer in new[] { "floor", "edge", "ceiling" })
            {
                var records = plan.GetArrayOrEmpty(layer == "edge" ? "placements" : layer + "_placements");
                if (records.IsEmpty) return false;
                foreach (object value in records)
                {
                    if (!(value is GdDict row)) return false;
                    string key = layer == "edge" ? row.GetString("edge_key", row.GetString("key")) : row.GetString("cell_key");
                    if (key.Length == 0 || !keys.Add(layer+"/"+key)) return false;
                    var owners = row.GetArrayOrEmpty("room_ids").Cast<object>().Select(V.Str).Where(owner => owner.Length > 0).ToList();
                    if (row.GetString("room_id").Length > 0) owners.Add(row.GetString("room_id"));
                    if (owners.Count == 0 || owners.Any(owner => !layout.GetArrayOrEmpty("rooms").Cast<GdDict>().Any(room => room.GetString("id") == owner))) return false;
                }
            }
            return true;
        }
        static bool BindOwnedSlot(GdDict row, GdDict room, GdArray cell)
        {
            if (cell.Count != 3 || V.I64(cell[2]) != room.GetInt("deck")) return false;
            var interior=room.GetDictOrEmpty("interior_zones");
            foreach (string kind in new[] { "center", "wall", "reserved" })
            {
                var slots=interior.GetArrayOrEmpty(kind=="reserved"?"reserved_cells":kind+"_slots");
                for (int index=0;index<slots.Count;index++)
                {
                    var parsed=LayoutSerializer.ParseSlotCell(slots[index]);
                    if (parsed.Count<2 || V.I64(parsed[0])!=V.I64(cell[0]) || V.I64(parsed[1])!=V.I64(cell[1])) continue;
                    row["approach_cell"]=cell.DeepCopy();row["slot_kind"]=kind;row["slot_index"]=(long)index;return true;
                }
            }
            return false;
        }
        static GdDict BuildGameplay(GdDict layout, FirstAwayGenerationInputs inputs)
        {
            var gameplay = new GameplaySliceBuilder().Build(layout);
            gameplay["generation_profile"] = FirstAwayGenerationInputs.Profile; gameplay["first_away_inputs"] = inputs.ToDict();
            var cargo = layout.GetArrayOrEmpty("rooms").Cast<GdDict>().Single(room => room.GetString("semantic_id") == "recovery");
            string cargoId = cargo.GetString("id"); var reservations = layout.GetDictOrEmpty("first_away_reservations");
            foreach (GdDict row in gameplay.GetArrayOrEmpty("loot_containers"))
            {
                row["id"] = row.GetString("room_id") + "/loot";
                if (row.GetString("room_id") == cargoId && !BindOwnedSlot(row,cargo,reservations.GetArrayOrEmpty("loot_approach"))) return new GdDict();
            }
            foreach (GdDict row in gameplay.GetArrayOrEmpty("objectives"))
            {
                row["id"] = row.GetString("room_id") + "/objective/" + row.GetString("type");
                if (row.GetString("room_id") == cargoId && !BindOwnedSlot(row,cargo,reservations.GetArrayOrEmpty("work_approach"))) return new GdDict();
            }
            // Explicit small common-material cache. It is a normal finite container, never an inventory grant.
            var bridge = layout.GetArrayOrEmpty("rooms").Cast<GdDict>().Last();
            var candidates = bridge.GetDictOrEmpty("interior_zones").GetArrayOrEmpty("wall_slots");
            if (candidates.IsEmpty) return new GdDict();
            var goal = gameplay.GetArrayOrEmpty("objectives").Cast<GdDict>().Single(row => row.GetString("room_id") == bridge.GetString("id"));
            var goalCell = LayoutSerializer.ParseSlotCell(goal.Get("approach_cell"));
            int cacheIndex = -1;
            for (int index=0;index<candidates.Count;index++)
            {
                var candidate=LayoutSerializer.ParseSlotCell(candidates[index]);
                if (candidate.Count>=2 && (V.I64(candidate[0])!=V.I64(goalCell[0]) || V.I64(candidate[1])!=V.I64(goalCell[1]))) { cacheIndex=index;break; }
            }
            if (cacheIndex<0) return new GdDict();
            var cell = LayoutSerializer.ParseSlotCell(candidates[cacheIndex]);
            long bonus = inputs.Substream("loot.common_cache_units") % 3;
            var contents = GdArray.Of(new GdDict { { "item_id", "scrap_metal" }, { "qty", 1L } });
            if (bonus > 0) contents.Append(new GdDict { { "item_id", "wiring_spool" }, { "qty", bonus } });
            gameplay.GetArrayOrEmpty("loot_containers").Append(new GdDict { { "id", bridge.GetString("id") + "/common_cache" },
                { "kind", "generic_locker" }, { "room_id", bridge.GetString("id") }, { "approach_cell", GdArray.Of(V.I64(cell[0]),V.I64(cell[1]),0L) },
                { "slot_kind", "wall" }, { "slot_index", (long)cacheIndex }, { "loot_table", "generic_locker" }, { "contents", contents }, { "content_budget", "first_away_common_cache_1_plus_0_to_2_v1" } });
            // At most one ordinary container per optional room plus this cache, and one builder objective per room.
            if (gameplay.GetArrayOrEmpty("loot_containers").Count > layout.GetArrayOrEmpty("rooms").Count - 2
                || gameplay.GetArrayOrEmpty("objectives").Count > layout.GetArrayOrEmpty("rooms").Count) return new GdDict();
            return gameplay;
        }
        internal static ShipDocuments AsDocuments(FirstAwayGenerationInputs inputs, GdDict layout, string kitPath,
            IDictionary<string,FirstAwayCatalogIdentity> catalogs)
        {
            // Persist the actual wrapper-bearing catalog identity, not a biome selector whose kit fell back.
            var kit = CatalogRegistry.LoadDict(kitPath);
            string resolvedKitId = kit?.GetString("kit_id") ?? "";
            if (kit == null || kit.GetArrayOrEmpty("modules").IsEmpty || !catalogs.ContainsKey(kitPath)
                || resolvedKitId.Length == 0 || kitPath != "res://data/kits/" + resolvedKitId + ".json") return null;
            layout["kit_id"] = resolvedKitId;
            var gameplay = BuildGameplay(layout, inputs); if (gameplay.IsEmpty) { CoreServices.Log.Error("First-away gameplay authoring empty"); return null; }
            var contract = new FirstRunContract(); if (!contract.LoadContract() || !FirstRunAwayGate.SatisfiesCompleteContract(contract, layout, gameplay, inputs.Condition)) { CoreServices.Log.Error("First-away contract: " + FirstRunAwayGate.RejectReason(contract,layout,gameplay,inputs.Condition)); return null; }
            string layoutJson = GdJson.Stringify(layout,"  "), gameplayJson = GdJson.Stringify(gameplay,"  ");
            var descriptor = new FirstAwayGenerationDescriptor(inputs, Provider, catalogs,
                FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(layoutJson), FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(gameplayJson));
            return new ShipDocuments { Layout = GdJson.ParseDict(layoutJson), GameplaySlice = GdJson.ParseDict(gameplayJson), SourceLayout = layout,
                LayoutJson = layoutJson, GameplaySliceJson = gameplayJson, Kit = kit.DeepCopy(), KitPath = kitPath, IsAway = true,
                RuntimeGeneratedGameplay = true, FirstAwayInputs = inputs, FirstAwayDescriptor = descriptor };
        }
        /// <summary>Strict archived admission against caller-authenticated inputs and pinned V1 catalogs/raw documents.</summary>
        public static bool TryRestore(FirstAwayGenerationInputs expected, GdDict snapshot, string layoutJson, string gameplayJson, out ShipDocuments documents)
        {
            documents = null; if (expected == null || snapshot == null || layoutJson == null || gameplayJson == null) return false;
            try
            {
                var catalogs = LoadPinnedCatalogs(); if (catalogs == null) return false;
                var descriptor = new FirstAwayGenerationDescriptor(expected,Provider,catalogs,FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(layoutJson),FirstAwayGenerationDescriptor.StrictUtf8.GetBytes(gameplayJson));
                if (!descriptor.MatchesSnapshot(snapshot)) return false;
                // Hash metadata alone is not source authority: a caller could recompute it for fabricated rewards.
                // Archived raw documents are initial authoring; runtime depletion/repair state is saved separately.
                // Regenerate only a pure expected witness with pinned V1 resources, compare exact bytes, return archived originals.
                var canonical = new ShipGenerator().GenerateFirstAway(expected);
                if (canonical == null || canonical.LayoutJson != layoutJson || canonical.GameplaySliceJson != gameplayJson
                    || !canonical.FirstAwayDescriptor.MatchesSnapshot(snapshot)) return false;
                var layout = GdJson.ParseDict(layoutJson); var gameplay = GdJson.ParseDict(gameplayJson);
                if (layout.IsEmpty || gameplay.IsEmpty || layout.GetString("generation_profile") != FirstAwayGenerationInputs.Profile || gameplay.GetString("generation_profile") != FirstAwayGenerationInputs.Profile) return false;
                // JSON numeric types differ after normal document loading; canonical embedded context must equal the exact expected serialization.
                string expectedJson = GdJson.Stringify(expected.ToDict());
                if (GdJson.Stringify(layout.GetDictOrEmpty("first_away_inputs")) != GdJson.Stringify(GdJson.ParseDict(expectedJson))
                    || GdJson.Stringify(gameplay.GetDictOrEmpty("first_away_inputs")) != GdJson.Stringify(GdJson.ParseDict(expectedJson))) return false;
                var contract = new FirstRunContract(); if (!contract.LoadContract() || !FirstRunAwayGate.SatisfiesCompleteContract(contract,layout,gameplay,expected.Condition)) return false;
                // The ordinary selector can fall back from a biome selector kit to the actual wrapper-bearing kit.
                string kitPath = canonical.KitPath;
                if (!catalogs.ContainsKey(kitPath)) return false;
                var kit = CatalogRegistry.LoadDict(kitPath); if (kit == null || kit.IsEmpty) return false;
                documents = new ShipDocuments { Layout=layout,GameplaySlice=gameplay,SourceLayout=layout,LayoutJson=layoutJson,GameplaySliceJson=gameplayJson,
                    IsAway=true,RuntimeGeneratedGameplay=true,Kit=kit.DeepCopy(),KitPath=kitPath,FirstAwayInputs=expected,FirstAwayDescriptor=descriptor };
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }
}
