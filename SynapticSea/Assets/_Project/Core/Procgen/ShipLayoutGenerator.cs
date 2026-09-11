// Ported from scripts/procgen/ship_layout_generator.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Top-level orchestrator for the procgen layout pipeline: TemplateSelector -&gt; RoomAssigner -&gt;
    /// CellLayoutEngine -&gt; WallDoorResolver -&gt; LayoutSerializer, then explicit footprints/portals, the optional
    /// EncounterInjector stage, condition mutators and the validated structural plan. Returns a complete layout.json
    /// dictionary (schema 1.2.0). Cells in the returned rooms are <see cref="Vec2i"/> like the GDScript.
    /// </summary>
    public sealed class ShipLayoutGenerator
    {
        public const long MAX_CONNECTIVITY_ATTEMPTS = 4;

        public TemplateSelector TemplateSelectorStage = new TemplateSelector();
        public RoomAssigner RoomAssignerStage = new RoomAssigner();
        public CellLayoutEngine CellLayoutEngineStage = new CellLayoutEngine();
        public WallDoorResolver WallDoorResolverStage = new WallDoorResolver();
        public LayoutSerializer LayoutSerializerStage = new LayoutSerializer();

        /// <summary>Created lazily on the first run with a biome and kept for later runs (GDScript behaviour).</summary>
        public RoomVariantSelector VariantSelector;

        public string BiomeId = "";
        public string DifficultyId = "";

        public GdDict Generate(ShipBlueprint blueprint, GdDict archetype = null) =>
            GenerateWithOptions(blueprint, archetype ?? new GdDict(), "", "", false);

        /// <summary>
        /// Extended entry point. Non-empty <paramref name="biomeId"/> / <paramref name="difficultyId"/> turn on variant
        /// selection and encounter injection; empty ids keep the legacy behaviour exactly.
        /// </summary>
        public GdDict GenerateWithOptions(
            ShipBlueprint blueprint,
            GdDict archetype = null,
            string biomeId = "",
            string difficultyId = "",
            bool extendedTemplates = false)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint), "ShipLayoutGenerator: blueprint must not be null");
            archetype = archetype ?? new GdDict();
            biomeId = biomeId ?? "";
            difficultyId = difficultyId ?? "";

            BiomeId = biomeId;
            DifficultyId = difficultyId;

            long baseSeed = blueprint.SeedValue;
            GdDict bestEffort = new GdDict();
            for (long attempt = 0; attempt < MAX_CONNECTIVITY_ATTEMPTS; attempt++)
            {
                // F2: deterministic seed salt on retry so bad connectivity is not sticky.
                long attemptSeed = attempt == 0 ? baseSeed : baseSeed ^ unchecked(attempt * 0x9E3779B9L);
                GdDict candidate;
                if (attempt > 0)
                {
                    long saved = blueprint.SeedValue;
                    blueprint.SeedValue = attemptSeed;
                    candidate = GenerateOnce(blueprint, archetype, biomeId, difficultyId, extendedTemplates);
                    blueprint.SeedValue = saved;
                }
                else
                {
                    candidate = GenerateOnce(blueprint, archetype, biomeId, difficultyId, extendedTemplates);
                }
                if (candidate.IsEmpty) continue;
                // Keep the best non-empty candidate across attempts.
                bestEffort = candidate;
                if (LayoutIsConnected(candidate)) return candidate;
            }
            if (!bestEffort.IsEmpty)
            {
                CoreServices.Log.Warning("ShipLayoutGenerator: layout connectivity soft-fail after " +
                                         GdString.FormatInt(MAX_CONNECTIVITY_ATTEMPTS) + " attempts seed=" + GdString.FormatInt(baseSeed));
                return bestEffort;
            }
            CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL all connectivity attempts empty");
            return new GdDict();
        }

        GdDict GenerateOnce(ShipBlueprint blueprint, GdDict archetype, string biomeId, string difficultyId, bool extendedTemplates)
        {
            // Stage 1: select topology template.
            TopologyTemplate template = extendedTemplates
                ? TemplateSelectorStage.SelectWithOptions(blueprint, archetype, true, true)
                : TemplateSelectorStage.Select(blueprint, archetype);
            if (template == null)
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL template selection returned null");
                return new GdDict();
            }

            // Stage 2: assign rooms to template zones (with variant selector).
            List<GdDict> roomPlan;
            if (VariantSelector == null && biomeId.Length != 0) VariantSelector = new RoomVariantSelector();
            roomPlan = VariantSelector != null
                ? RoomAssignerStage.AssignWithSelector(template, blueprint, archetype, VariantSelector, biomeId)
                : RoomAssignerStage.Assign(template, blueprint, archetype);
            if (roomPlan.Count == 0)
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL room assignment returned empty");
                return new GdDict();
            }

            // Stage 3: place rooms on 2D grid.
            GdDict cellGrid = CellLayoutEngineStage.Layout(roomPlan, template, blueprint.SeedValue);
            if (cellGrid.GetDictOrEmpty("rooms").IsEmpty)
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL cell layout returned empty rooms");
                return new GdDict();
            }

            // Stage 4: resolve walls, doors, interior zones.
            GdDict geometry = WallDoorResolverStage.Resolve(cellGrid, roomPlan);

            // Stage 5: serialize to layout.json format.
            string archetypeName = V.Str(archetype.Get("name", V.Str(archetype.Get("template", "default"))));
            GdDict layout = LayoutSerializerStage.Serialize(cellGrid, geometry, roomPlan, template.Id, blueprint.SeedValue, archetypeName);

            // The structural compiler consumes solved logical footprints; stamp the exact cells and emit portal intents
            // only for real shared cardinal edges.
            if (!StampExplicitStructuralLayout(layout, cellGrid))
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL invalid adjacency/footprint boundary");
                return new GdDict();
            }

            // Stage 6 (optional): inject encounter markers when biome and/or difficulty are non-empty.
            if (biomeId.Length != 0 || difficultyId.Length != 0)
            {
                GdDict biomeData = ResolveBiome(biomeId);
                GdDict difficultyData = ResolveDifficulty(difficultyId);
                BiomeProfile biome = BiomeProfile.FromDict(biomeData);
                DifficultyProfile difficulty = DifficultyProfile.FromDict(difficultyData);
                layout = new EncounterInjector().Inject(layout, biome, difficulty, blueprint.SeedValue);
            }

            // Stamp template_id / biome / difficulty / kit_id / hazard authority on the layout.
            layout["template_id"] = template.Id;
            if (biomeId.Length != 0) layout["biome_id"] = biomeId;
            if (template.Id == "hive") layout["kit_id"] = "ship_structural_biomatter";
            else if (biomeId.Length != 0) layout["kit_id"] = KitIdForBiome(biomeId);
            else if (V.Str(layout.Get("kit_id", "")).Length == 0) layout["kit_id"] = "ship_structural_v0";
            if (difficultyId.Length != 0) layout["difficulty_id"] = difficultyId;
            // F4: runtime coordinator owns fire/breach seeding for derelicts.
            layout["hazard_source"] = "runtime";

            // Overlay locks/breaches before compile; recompile after stamping; wreck lands after the last compile.
            ApplyConditionMutators(layout, blueprint);
            if (!StampStructuralPlan(layout))
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL structural plan validation failed");
                return new GdDict();
            }
            ApplyWreckToCompiledPlan(layout, blueprint);

            return layout;
        }

        static void ApplyConditionMutators(GdDict layout, ShipBlueprint blueprint)
        {
            if (blueprint == null) return;
            long condition = blueprint.ShipCondition;
            if (condition == (long)ShipBlueprint.Condition.Pristine) return;
            long seedValue = blueprint.SeedValue;
            LayoutMutator.ApplyBranchOverlays(layout, seedValue);
            bool wrecked = condition == (long)ShipBlueprint.Condition.Wrecked;
            LayoutMutator.ApplyPortalOverlays(layout, seedValue, wrecked);
        }

        static bool StampStructuralPlan(GdDict layout)
        {
            GdDict structuralPlan = new StructuralEdgeCompiler().Compile(layout);
            GdDict verdict = new StructuralPlanValidator().Validate(structuralPlan, layout);
            if (!V.Bool(verdict.Get("ok", false)))
            {
                CoreServices.Log.Error("SHIP LAYOUT GENERATOR FAIL structural plan validation failed: " +
                                       GdJson.Stringify(verdict.Get("errors", new GdArray())));
                layout["structural_plan_validated"] = false;
                return false;
            }
            layout["structural_plan"] = structuralPlan;
            layout["structural_plan_validated"] = true;
            return true;
        }

        static void ApplyWreckToCompiledPlan(GdDict layout, ShipBlueprint blueprint)
        {
            if (blueprint == null) return;
            long condition = blueprint.ShipCondition;
            if (condition == (long)ShipBlueprint.Condition.Pristine) return;
            double frac = 0.35;
            if (condition == (long)ShipBlueprint.Condition.Wrecked) frac = 0.55;
            LayoutMutator.ApplyWreckToCompiledPlan(layout, blueprint.SeedValue, null, frac);
        }

        static bool StampExplicitStructuralLayout(GdDict layout, GdDict cellGrid)
        {
            GdDict placedRooms = cellGrid.GetDictOrEmpty("rooms");
            foreach (var roomVariant in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length == 0 || !placedRooms.Has(roomId)) continue;
                var solved = (GdDict)placedRooms[roomId];
                var cells = new GdArray();
                foreach (var cellVariant in solved.GetArrayOrEmpty("cells"))
                    if (cellVariant is Vec2i) cells.Append(cellVariant);
                string role = V.Str(solved.Get("role", room.Get("room_role", "")));
                long deck = V.I64(solved.Get("deck", room.Get("deck", 0L)));
                Vec2i footprint = solved.Get("footprint") is Vec2i fp ? fp : Vec2i.Zero;
                room["role"] = role;
                // Keep room_role for existing gameplay/loader consumers; the canonical role field is explicit.
                room["room_role"] = role;
                room["deck"] = deck;
                room["cells"] = cells;
                room["footprint"] = footprint;
            }

            GdArray rawAdjacencies = cellGrid.GetArrayOrEmpty("adjacencies");
            // Keep the pre-dedup topology available to validation/stress code.
            layout["adjacency_intents"] = CopyAdjacencyIntents(rawAdjacencies);
            if (!BuildExplicitPortals(rawAdjacencies, placedRooms, out GdArray portals)) return false;
            layout["portals"] = portals;
            // Physical seams only; room_links remains the backwards-compatible logical graph.
            layout["structural_room_links"] = portals.DeepCopy();
            return true;
        }

        static bool BuildExplicitPortals(GdArray adjacencies, GdDict placedRooms, out GdArray portals)
        {
            portals = new GdArray();
            var emittedEdges = new HashSet<string>();
            var errors = new List<string>();
            foreach (var adjacencyVariant in adjacencies)
            {
                if (!(adjacencyVariant is GdDict adjacency))
                {
                    errors.Add("adjacency intent is not a Dictionary");
                    continue;
                }
                string fromRoom = V.Str(adjacency.Get("from_room", ""));
                string toRoom = V.Str(adjacency.Get("to_room", ""));
                if (fromRoom.Length == 0 || toRoom.Length == 0)
                {
                    errors.Add("adjacency intent is missing a room endpoint");
                    continue;
                }
                if (!placedRooms.Has(fromRoom) || !placedRooms.Has(toRoom))
                {
                    errors.Add("adjacency intent references an unknown room: " + fromRoom + " -> " + toRoom);
                    continue;
                }
                var fromData = (GdDict)placedRooms[fromRoom];
                var toData = (GdDict)placedRooms[toRoom];
                long fromDeck = V.I64(fromData.Get("deck", 0L));
                long toDeck = V.I64(toData.Get("deck", 0L));
                if (!IntegerCell(adjacency.Get("from_cell", null), out Vec2i fromCell) ||
                    !IntegerCell(adjacency.Get("to_cell", null), out Vec2i toCell))
                {
                    errors.Add("adjacency intent has invalid integer endpoint cells: " + fromRoom + " -> " + toRoom);
                    continue;
                }
                if (!RoomContainsCell(fromData, fromCell))
                {
                    errors.Add("adjacency intent source cell is not in room footprint: " + fromRoom + " " + fromCell);
                    continue;
                }
                if (!RoomContainsCell(toData, toCell))
                {
                    errors.Add("adjacency intent target cell is not in room footprint: " + toRoom + " " + toCell);
                    continue;
                }
                // A vertical connection has no cardinal shared edge; it stays in vertical_connections.
                if (fromDeck != toDeck) continue;
                string direction = DirectionBetween(fromCell, toCell);
                if (direction.Length == 0)
                {
                    errors.Add("adjacency intent endpoints do not share a cardinal boundary: " + fromRoom + " -> " + toRoom);
                    continue;
                }
                string declaredDirection = V.Str(adjacency.Get("direction", adjacency.Get("wall", adjacency.Get("from_direction", ""))));
                if (declaredDirection.Length != 0 && declaredDirection != direction)
                {
                    errors.Add("adjacency intent declared boundary " + declaredDirection + " but endpoints are " + direction + ": " +
                               fromRoom + " -> " + toRoom);
                    continue;
                }
                string edgeKey = StructuralEdgePlan.EdgeKey(fromDeck, fromCell, direction);
                string declaredEdgeKey = V.Str(adjacency.Get("edge_key", ""));
                if (declaredEdgeKey.Length != 0 && declaredEdgeKey != edgeKey)
                {
                    errors.Add("adjacency intent declared edge " + declaredEdgeKey + " but endpoints resolve to " + edgeKey);
                    continue;
                }
                if (!emittedEdges.Add(edgeKey)) continue;
                object intentType = "door";
                if (adjacency.Has("type")) intentType = adjacency["type"];
                else if (adjacency.Has("portal_type")) intentType = adjacency["portal_type"];
                object required = !adjacency.Has("required") ? (object)true : adjacency["required"];
                portals.Append(new GdDict
                {
                    { "id", "portal:" + edgeKey },
                    { "from_room", fromRoom },
                    { "to_room", toRoom },
                    // Keep both accepted intent spellings so LOCKED/HATCH/etc. are not flattened to the door default.
                    { "type", intentType },
                    { "portal_type", intentType },
                    { "required", required },
                    { "edge_key", edgeKey },
                    { "deck", fromDeck },
                    { "cell", fromCell },
                    { "direction", direction },
                    { "opposite_direction", StructuralEdgePlan.OPPOSITE[direction] },
                    { "from_cell", fromCell },
                    { "to_cell", toCell },
                    { "source_cells", GdArray.Of(fromCell, toCell) },
                });
            }
            return errors.Count == 0;
        }

        static GdArray CopyAdjacencyIntents(GdArray adjacencies)
        {
            var intents = new GdArray();
            foreach (var adjacencyVariant in adjacencies)
            {
                if (!(adjacencyVariant is GdDict adjacency))
                {
                    intents.Append(adjacencyVariant);
                    continue;
                }
                GdDict intent = adjacency.DeepCopy();
                // Absence means required; an authored false stays false.
                if (!intent.Has("required")) intent["required"] = true;
                intents.Append(intent);
            }
            return intents;
        }

        static bool RoomContainsCell(GdDict roomData, Vec2i cell)
        {
            if (!(roomData.Get("cells", new GdArray()) is GdArray rawCells)) return false;
            foreach (var rawCell in rawCells)
                if (IntegerCell(rawCell, out Vec2i c) && c == cell) return true;
            return false;
        }

        static bool IntegerCell(object rawCell, out Vec2i cell)
        {
            cell = Vec2i.Zero;
            if (rawCell is Vec2i v)
            {
                cell = v;
                return true;
            }
            if (rawCell is GdArray values && values.Count == 2 && values[0] is long x && values[1] is long y)
            {
                cell = new Vec2i((int)x, (int)y);
                return true;
            }
            return false;
        }

        static string DirectionBetween(Vec2i fromCell, Vec2i toCell)
        {
            Vec2i delta = toCell - fromCell;
            foreach (var directionVariant in StructuralEdgePlan.DIRECTIONS.Keys)
            {
                string direction = V.Str(directionVariant);
                if ((Vec2i)StructuralEdgePlan.DIRECTIONS[direction] == delta) return direction;
            }
            return "";
        }

        /// <summary>Room-link connectivity: every room id must be reachable from prototype.start_room.</summary>
        public static bool LayoutIsConnected(GdDict layout)
        {
            if (!(layout.Get("rooms", new GdArray()) is GdArray rooms) || rooms.IsEmpty) return false;
            var roomIds = new List<string>();
            foreach (var r in rooms)
            {
                if (r is GdDict rd)
                {
                    string rid = V.Str(rd.Get("id", ""));
                    if (rid.Length != 0) roomIds.Add(rid);
                }
            }
            if (roomIds.Count == 0) return false;
            string start = V.Str(layout.GetDictOrEmpty("prototype").Get("start_room", roomIds[0]));
            if (start.Length == 0) start = roomIds[0];
            var adj = new GdDict();
            foreach (string rid in roomIds) adj[rid] = new GdArray();
            if (layout.Get("room_links", new GdArray()) is GdArray links)
            {
                foreach (var linkVariant in links)
                {
                    if (!(linkVariant is GdDict link)) continue;
                    string a = V.Str(link.Get("from_room", ""));
                    string b = V.Str(link.Get("to_room", ""));
                    if (a.Length == 0 || b.Length == 0) continue;
                    if (!adj.Has(a)) adj[a] = new GdArray();
                    if (!adj.Has(b)) adj[b] = new GdArray();
                    ((GdArray)adj[a]).Append(b);
                    ((GdArray)adj[b]).Append(a);
                }
            }
            var seen = new HashSet<string> { start };
            var q = new List<string> { start };
            int head = 0;
            while (head < q.Count)
            {
                string cur = q[head++];
                foreach (var nxt in adj.GetArrayOrEmpty(cur))
                {
                    string n = V.Str(nxt);
                    if (!seen.Add(n)) continue;
                    q.Add(n);
                }
            }
            return seen.Count >= roomIds.Count;
        }

        /// <summary>KitCatalog biome preference: enclosed rooms, remapped stems.</summary>
        public static string KitIdForBiome(string biome)
        {
            switch (biome)
            {
                case "breach_field": return "ship_structural_hazard";
                case "dead_fleet": return "ship_structural_industrial";
                default: return "ship_structural_v0";
            }
        }

        /// <summary>
        /// GDScript <c>_resolve_biome</c> (also used by ShipGenerator): the biome JSON when present, else built-in
        /// defaults; an empty id resolves to a minimal abyssal_synaptic_sea dictionary.
        /// </summary>
        public GdDict ResolveBiome(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return new GdDict { { "id", "abyssal_synaptic_sea" } };
            string relPath = "res://data/procgen/biomes/" + biomeId + ".json";
            if (CatalogRegistry.Exists(relPath))
            {
                GdDict parsed = CatalogRegistry.LoadDict(relPath);
                if (parsed != null) return parsed;
            }
            switch (biomeId)
            {
                case "breach_field":
                    return new GdDict
                    {
                        { "id", "breach_field" },
                        { "hazard_modifier", 1.4 },
                        { "loot_quality_modifier", 1.1 },
                        { "encounter_density_modifier", 1.3 },
                        { "ambient_intensity", 0.85 },
                        { "encounter_table_id", "biomatter_lurker" },
                    };
                case "dead_fleet":
                    return new GdDict
                    {
                        { "id", "dead_fleet" },
                        { "hazard_modifier", 1.1 },
                        { "loot_quality_modifier", 1.4 },
                        { "encounter_density_modifier", 0.8 },
                        { "ambient_intensity", 1.1 },
                        { "encounter_table_id", "derelict_pirate" },
                    };
                default:
                    return new GdDict
                    {
                        { "id", "abyssal_synaptic_sea" },
                        { "hazard_modifier", 1.0 },
                        { "loot_quality_modifier", 1.0 },
                        { "encounter_density_modifier", 1.0 },
                        { "ambient_intensity", 1.0 },
                        { "encounter_table_id", "biomatter_lurker" },
                    };
            }
        }

        /// <summary>GDScript <c>_resolve_difficulty</c>: <see cref="DifficultyProfile.ResolveDict"/>.</summary>
        public GdDict ResolveDifficulty(string difficultyId) => DifficultyProfile.ResolveDict(difficultyId);
    }
}
