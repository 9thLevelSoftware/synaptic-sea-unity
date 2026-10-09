using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Phase 1.9: what a generated New Run home must hand the survivor. The survivor can only leave the home when the always-attached
    /// life boat is repaired, so every part and tool that repair needs is placed in the home (the places vary by seed, the items never do),
    /// together with food and water for the first excursion and a calm start: no encounters, arcs, breaches, fires or hazardous room variants.
    /// <para><see cref="Compute"/> is pure: the damage the session will roll (<see cref="HomeOpeningState"/>) decides the kit, and the live survival
    /// rates (<c>balance/survival.json</c> through <see cref="SurvivalTuning"/>) decide the food and water, so the guarantee cannot drift from the
    /// game. <see cref="Apply"/> places the plan in seeded rooms and fails closed (a reason string) so <see cref="StartSceneBuilder.BuildHomeStart"/>
    /// rolls the next seed; <see cref="Validate"/> re-derives the plan from the finished documents and checks it independently.</para>
    /// Generated homes only. The golden hub keeps its authored caches.
    /// </summary>
    public static class StartingHomeGuarantee
    {
        public const string RepairCacheAId = "home_repair_cache_a";
        public const string RepairCacheBId = "home_repair_cache_b";
        public const string SurvivalStoresId = "home_survival_stores";
        public static readonly string[] ContainerIds = { RepairCacheAId, RepairCacheBId, SurvivalStoresId };

        /// <summary>Failure reasons (the prefix before any detail).</summary>
        public const string ReasonScaleUnsupported = "scale_unsupported";
        public const string ReasonCapacity = "capacity_unsatisfiable";
        public const string ReasonOverweight = "kit_overweight";
        public const string ReasonNoFreeSlot = "no_free_slot";
        public const string ReasonHazardEdges = "hazard_edges";

        /// <summary>The life boat has three floor cells (engine bay, airlock, cockpit).</summary>
        public static int LifeboatFloorCells
        {
            get
            {
                int cells = AssemblyMobility.Floors(LifeBoatBuilder.BuildLayout()).Count;
                return cells > 0 ? cells : 3;
            }
        }

        /// <summary>The life boat's own hull is never configured, so its hull factor is full integrity.</summary>
        const double LifeboatHullIntegrity = 1.0;
        const int PlacementSalt = 0x57A27;
        static readonly string[] TravelSystems = { "power", "navigation", "propulsion" };
        /// <summary>Parts light enough to carry a spare of (one more than the repairs consume).</summary>
        static readonly HashSet<string> SpareParts = new HashSet<string>(StringComparer.Ordinal)
            { "circuit_board", "power_cell", "data_core", "sensor_module", "fuel_line" };
        /// <summary>The flight parts: the home always holds one beyond what its own repairs consume (OPEN-3), for the later joined-home flight.</summary>
        static readonly string[] FlightParts = { "thruster_nozzle", "fuel_line" };

        public sealed class Spec
        {
            /// <summary>Game hours the food and water must carry the survivor through the first excursion.</summary>
            public double GameHours = 30.0;
            /// <summary>
            /// Safety factor on the food and water: stale food restores 60% (<c>stale_multiplier</c>), and temperature and wounds raise thirst. 1.7 keeps the
            /// floors even when only 60% of the stores do their work (1 / 0.6 = 1.67, plus the thirst the rations then no longer cover).
            /// </summary>
            public double Margin = 1.8;
            /// <summary>The run's clock scale. Real-time pacing (1.0) is refused: its rates make a 30 minute excursion impossible to supply.</summary>
            public double ClockScale = WorldClock.DefaultNewRunScale;
            /// <summary>Hunger and thirst must stay at or above these after the excursion.</summary>
            public double HungerFloorPercent = 25.0;
            public double ThirstFloorPercent = 40.0;
            /// <summary>Null loads <c>balance/survival.json</c>; tests inject tunings to prove the rates are read live.</summary>
            public SurvivalTuning Tuning;
            /// <summary>No single container may hold more than this (a bag holds <see cref="InventoryState.MAX_WEIGHT"/>).</summary>
            public double MaxContainerKg = 0.6 * InventoryState.MAX_WEIGHT;
            /// <summary>Repair skill assumed by the capacity backstop: the weakest survivor.</summary>
            public long RepairSkill = 0;
            /// <summary>Extra cargo in the capacity backstop (test hook).</summary>
            public double ExtraCargoKg = 0.0;
        }

        public sealed class Item
        {
            public string Id = "";
            public long Qty;
            public double UnitKg;
            public double TotalKg => Qty * UnitKg;
        }

        public sealed class Plan
        {
            public bool Ok;
            public string Reason = "";
            /// <summary>Power, navigation and propulsion parts the opening damage leaves broken ("power.battery_cells").</summary>
            public readonly List<string> BrokenSubs = new List<string>();
            public readonly List<Item> CacheA = new List<Item>();
            public readonly List<Item> CacheB = new List<Item>();
            public readonly List<Item> Stores = new List<Item>();
            public long Rations;
            public long Water;
            public double RepairKitKg;
            public double StoresKg;
            public double CapacityMarginKg;

            public IEnumerable<Item> AllItems => CacheA.Concat(CacheB).Concat(Stores);

            public Dictionary<string, long> Totals()
            {
                var totals = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (Item item in AllItems) totals[item.Id] = (totals.TryGetValue(item.Id, out long n) ? n : 0) + item.Qty;
                return totals;
            }
        }

        static Plan Fail(Plan plan, string reason)
        {
            plan.Ok = false;
            plan.Reason = reason;
            return plan;
        }

        // ------------------------------------------------------------------ the pure plan

        public static Plan Compute(ShipBlueprint blueprint, Spec spec) => Compute(HomeOpeningState.Build(blueprint), spec);

        /// <summary>The kit, food and water a home with <paramref name="opening"/> ship-systems damage must hold. Pure and deterministic.</summary>
        public static Plan Compute(ShipSystemsManager opening, Spec spec)
        {
            spec = spec ?? new Spec();
            var plan = new Plan();
            if (opening == null) return Fail(plan, "no_opening_state");
            var clock = new WorldClock();
            clock.SetScale(spec.ClockScale);
            if (clock.IsRealTime) return Fail(plan, ReasonScaleUnsupported);
            GdDict defs = ItemDefs.LoadDefinitions();

            // 1. repair kit: one copy of each required part per broken travel part, tools once.
            var need = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var tools = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string systemId in TravelSystems)
            {
                ShipSystem system = opening.GetSystem(systemId);
                if (system == null) continue;
                foreach (ShipSubcomponent sub in system.Subcomponents)
                {
                    if (sub.IsFunctional()) continue;
                    plan.BrokenSubs.Add(systemId + "." + sub.SubcomponentId);
                    foreach (string part in sub.RequiredParts) need[part] = (need.TryGetValue(part, out long n) ? n : 0) + 1;
                    foreach (string tool in sub.RequiredTools) tools.Add(tool);
                }
            }
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> entry in need) counts[entry.Key] = entry.Value + (SpareParts.Contains(entry.Key) ? 1 : 0);
            foreach (string flight in FlightParts)
            {
                long consumed = need.TryGetValue(flight, out long n) ? n : 0;
                counts[flight] = Math.Max(counts.TryGetValue(flight, out long c) ? c : 0, consumed + 1);
            }
            foreach (string tool in tools) counts[tool] = 1;

            var units = new List<Item>();
            foreach (KeyValuePair<string, long> entry in counts)
            {
                if (!TryWeight(defs, entry.Key, out double kg)) return Fail(plan, "unknown_item:" + entry.Key);
                for (long i = 0; i < entry.Value; i++) units.Add(new Item { Id = entry.Key, Qty = 1, UnitKg = kg });
            }

            // 2. two repair caches, balanced by weight (heaviest first onto the lighter cache).
            double weightA = 0, weightB = 0;
            foreach (Item unit in units.OrderByDescending(u => u.UnitKg).ThenBy(u => u.Id, StringComparer.Ordinal))
            {
                if (weightA <= weightB) { Add(plan.CacheA, unit); weightA += unit.UnitKg; }
                else { Add(plan.CacheB, unit); weightB += unit.UnitKg; }
            }
            plan.RepairKitKg = weightA + weightB;

            // 3. food and water from the live rates.
            SurvivalTuning tuning = spec.Tuning ?? SurvivalTuning.FromDict(CatalogRegistry.LoadDict(SurvivalTuning.Path));
            var vitals = new VitalsState();
            tuning.ApplyTo(vitals, clock);
            double hungerPerHour = vitals.HungerDrainRate * WorldClock.SecondsPerHour;
            double thirstPerHour = vitals.ThirstDrainRate * WorldClock.SecondsPerHour;
            GdDict ration = ItemDefs.GetDefinition(defs, "ration_pack");
            GdDict water = ItemDefs.GetDefinition(defs, "purified_water");
            double rationHunger = ration.GetFloat("hunger_restore");
            double rationThirst = ration.GetFloat("thirst_restore");
            double waterThirst = water.GetFloat("thirst_restore");
            if (ration.IsEmpty || water.IsEmpty || rationHunger <= 0 || waterThirst <= 0) return Fail(plan, "unknown_item:ration_pack_or_purified_water");
            double hungerBudget = 100.0 - spec.HungerFloorPercent;
            double thirstBudget = 100.0 - spec.ThirstFloorPercent;
            plan.Rations = (long)Math.Ceiling(Math.Max(0.0, hungerPerHour * spec.GameHours - hungerBudget) / rationHunger * spec.Margin);
            plan.Water = (long)Math.Ceiling(Math.Max(0.0, thirstPerHour * spec.GameHours - thirstBudget - plan.Rations * rationThirst) / waterThirst * spec.Margin);
            foreach ((string id, long qty) in new[] { ("ration_pack", plan.Rations), ("purified_water", plan.Water), ("field_medkit", 1L), ("bandage_kit", 1L) })
            {
                if (qty <= 0) continue;
                if (!TryWeight(defs, id, out double kg)) return Fail(plan, "unknown_item:" + id);
                plan.Stores.Add(new Item { Id = id, Qty = qty, UnitKg = kg });
            }
            plan.StoresKg = plan.Stores.Sum(i => i.TotalKg);

            // 4. nothing may exceed a container's cap.
            if (plan.CacheA.Sum(i => i.TotalKg) > spec.MaxContainerKg || plan.CacheB.Sum(i => i.TotalKg) > spec.MaxContainerKg || plan.StoresKg > spec.MaxContainerKg)
                return Fail(plan, ReasonOverweight);

            // 5. capacity backstop: the weakest survivor repairs every broken travel part and carries everything.
            var sim = new ShipSystemsManager();
            sim.Configure(sim.LoadDefinitions(), 0, 0);
            sim.ApplySummary(opening.GetSummary());
            foreach (string systemId in TravelSystems)
            {
                ShipSystem system = sim.GetSystem(systemId);
                if (system == null) continue;
                foreach (ShipSubcomponent sub in system.Subcomponents)
                    if (!sub.IsFunctional()) sub.Health = sub.RepairQuality(spec.RepairSkill);
            }
            if (!sim.IsOperational("power") || !sim.IsOperational("propulsion")) return Fail(plan, ReasonCapacity + ":flight_path_not_operational");
            double area = LifeboatFloorCells * AssemblyMobility.AreaPerFloorM2;
            double supported = AssemblyMobility.EffectiveSupportedKg(AssemblyMobility.RatedSupportedKg(area),
                sim.GetSystem("propulsion").Health(), sim.GetSystem("power").Health(), LifeboatHullIntegrity);
            double mass = AssemblyMobility.DryMassKg(area) + plan.RepairKitKg + plan.StoresKg + spec.ExtraCargoKg;
            plan.CapacityMarginKg = supported - mass;
            if (plan.CapacityMarginKg < 0.0) return Fail(plan, ReasonCapacity);

            plan.Ok = true;
            return plan;
        }

        static void Add(List<Item> stacks, Item unit)
        {
            Item existing = stacks.FirstOrDefault(s => s.Id == unit.Id);
            if (existing != null) existing.Qty += unit.Qty;
            else stacks.Add(new Item { Id = unit.Id, Qty = unit.Qty, UnitKg = unit.UnitKg });
            stacks.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        }

        static bool TryWeight(GdDict defs, string itemId, out double kg)
        {
            GdDict def = ItemDefs.GetDefinition(defs, itemId);
            kg = def.GetFloat("weight");
            return !def.IsEmpty && kg > 0.0;
        }

        // ------------------------------------------------------------------ placement

        static string RoleOf(GdDict room)
        {
            string role = V.Str(room.Get("room_role", ""));
            return role.Length != 0 ? role : V.Str(room.Get("role", ""));
        }

        /// <summary>The first hazard edge or link of the layout, as a rejection reason; "" when the layout has none.</summary>
        public static string HazardEdgeReason(GdDict layout)
        {
            if (layout == null) return "no layout";
            if (!layout.GetArrayOrEmpty("blocked_links").IsEmpty) return ReasonHazardEdges + ":blocked_links";
            if (!layout.GetArrayOrEmpty("module_damage").IsEmpty) return ReasonHazardEdges + ":module_damage";
            foreach (object edgeV in layout.GetDictOrEmpty("structural_plan").GetDictOrEmpty("edges").Values)
            {
                if (!(edgeV is GdDict edge)) continue;
                string kind = WalkabilityContract.EdgeKind(edge);
                if (kind == "LOCKED" || kind == "BREACH") return ReasonHazardEdges + ":" + kind.ToLowerInvariant();
            }
            return "";
        }

        static bool Reaches(GdDict layout, string from, string to)
        {
            if (from.Length == 0 || to.Length == 0 || from == to) return true;
            GdDict plan = layout.GetDictOrEmpty("structural_plan");
            GdDict occupancy = plan.GetDictOrEmpty("occupancy");
            GdDict adjacency = WalkabilityContract.BuildAdjacency(occupancy, plan.GetDictOrEmpty("edges"), layout, true);
            return WalkabilityContract.RoomsReachable(adjacency, occupancy, from, to);
        }

        /// <summary>
        /// Rooms that can hold a cache: a non-connective room other than the start room and the dock/airlock room, standing-reachable from the
        /// start room and from which the dock/airlock room is standing-reachable.
        /// </summary>
        public static List<string> EligibleRooms(GdDict layout, GdDict slice)
        {
            string start = V.Str(slice.Get("start_room", ""));
            string dock = HomeDockPlanner.DockRoomId(layout);
            var connective = new HashSet<string>(GameplaySliceBuilder.CONNECTIVE_ROLES);
            var rooms = new List<string>();
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomV is GdDict room)) continue;
                string id = V.Str(room.Get("id", ""));
                if (id.Length == 0 || id == start || id == dock || connective.Contains(RoleOf(room))) continue;
                if (!Reaches(layout, start, id) || !Reaches(layout, id, dock)) continue;
                rooms.Add(id);
            }
            return rooms;
        }

        static GdArray Contents(IEnumerable<Item> items)
        {
            var contents = new GdArray();
            foreach (Item item in items) contents.Append(FirstRunAwayGate.Stack(item.Id, item.Qty));
            return contents;
        }

        /// <summary>
        /// Places the plan's three containers in seeded eligible rooms of <paramref name="docs"/> (appended after the existing containers, so their
        /// rolls are untouched), strips the home's hazards, and brings every mirror (layout, source layout, slice, and both JSON texts) up to date.
        /// "" on success, else the rejection reason; the documents are only changed on success.
        /// </summary>
        public static string Apply(ShipDocuments docs, ShipBlueprint blueprint, long seed, Spec spec, out Plan plan)
        {
            plan = Compute(blueprint, spec);
            if (!plan.Ok) return plan.Reason;
            if (docs?.Layout == null || docs.GameplaySlice == null) return "no documents";
            string hazard = HazardEdgeReason(docs.Layout);
            if (hazard.Length != 0) return hazard;

            List<string> eligible = EligibleRooms(docs.Layout, docs.GameplaySlice);
            GodotRandom rng = GodotRandom.FromSeed(FirstRunAwayGate.DeriveSeed(seed, PlacementSalt));
            for (int i = eligible.Count - 1; i > 0; i--)
            {
                int j = (int)rng.RandiRange(0, i);
                (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
            }

            GdDict slice = docs.GameplaySlice;
            // Work on a copy of the slice's container list so a failed placement leaves the documents untouched.
            GdDict probe = slice.DeepCopy();
            GdArray containers = probe.Get("loot_containers", null) as GdArray;
            if (containers == null) { containers = new GdArray(); probe["loot_containers"] = containers; }
            var used = new HashSet<string>(StringComparer.Ordinal);
            var specs = new (string Id, List<Item> Items)[] { (RepairCacheAId, plan.CacheA), (RepairCacheBId, plan.CacheB), (SurvivalStoresId, plan.Stores) };
            foreach ((string id, List<Item> items) in specs)
            {
                bool placed = false;
                foreach (string roomId in eligible)
                {
                    if (used.Contains(roomId)) continue;
                    if (!FirstRunAwayGate.TryFindFreeSlot(docs.Layout, probe, roomId, out GdArray cell, out string slotKind, out long slotIndex, out long deck)) continue;
                    containers.Append(new GdDict
                    {
                        { "id", id },
                        { "kind", "generic_crate" },
                        { "room_id", roomId },
                        { "approach_cell", GdArray.Of(V.I64(cell[0]), V.I64(cell[1]), deck) },
                        { "loot_table", "generic_crate" },
                        { "slot_kind", slotKind },
                        { "slot_index", slotIndex },
                        { "contents", Contents(items) },
                    });
                    used.Add(roomId);
                    placed = true;
                    break;
                }
                if (!placed) return ReasonNoFreeSlot + ":" + id;
            }

            // Commit: the finished containers go onto the real slice, then the hazard strip, then every text mirror.
            GdArray real = slice.Get("loot_containers", null) as GdArray;
            if (real == null) { real = new GdArray(); slice["loot_containers"] = real; }
            for (int i = real.Count; i < containers.Count; i++) real.Append(((GdDict)containers[i]).DeepCopy());
            StripHazards(slice, docs.Layout);
            if (docs.SourceLayout != null && !ReferenceEquals(docs.SourceLayout, docs.Layout)) StripHazards(null, docs.SourceLayout);
            docs.LayoutJson = GdJson.Stringify(docs.Layout, "  ");
            docs.GameplaySliceJson = GdJson.Stringify(docs.GameplaySlice, "  ");
            // The generator hands out documents parsed back from their JSON text (ShipGenerator.LoadLayoutAsDocuments), so numbers have the text's
            // types. Re-parse so the mutated documents keep that invariant and agree with the text mirrors exactly.
            docs.Layout = GdJson.ParseDict(docs.LayoutJson);
            docs.GameplaySlice = GdJson.ParseDict(docs.GameplaySliceJson);
            return "";
        }

        /// <summary>The calm start: no encounters, arc zones, fire zones or breach zones, and every room standard.</summary>
        static void StripHazards(GdDict slice, GdDict layout)
        {
            foreach (string key in new[] { "encounters", "arc_zones", "fire_zones", "breach_zones" })
            {
                if (layout != null && layout.Has(key)) layout[key] = new GdArray();
                if (slice != null && slice.Has(key)) slice[key] = new GdArray();
            }
            if (layout == null) return;
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
                if (roomV is GdDict room && room.Has("variant") && V.Str(room.Get("variant", "standard")) != RoomVariantSelector.VARIANT_STANDARD)
                    room["variant"] = RoomVariantSelector.VARIANT_STANDARD;
        }

        // ------------------------------------------------------------------ independent check

        /// <summary>
        /// Re-derives the plan from <paramref name="blueprint"/> and checks the finished documents against it: every mirror agrees on the
        /// containers, the containers follow the existing ones in three distinct eligible rooms on real loot slots, everything the plan needs is
        /// there, nothing hazardous remains, and the caches are walkable from the start room to the dock. "" when the home keeps its promises.
        /// </summary>
        public static string Validate(ShipDocuments docs, ShipBlueprint blueprint, Spec spec)
        {
            Plan plan = Compute(blueprint, spec);
            if (!plan.Ok) return plan.Reason;
            if (docs?.Layout == null || docs.GameplaySlice == null) return "no documents";
            GdDict slice = docs.GameplaySlice;
            GdArray containers = slice.GetArrayOrEmpty("loot_containers");

            // mirrors agree
            var ids = containers.OfType<GdDict>().Select(c => V.Str(c.Get("id", ""))).ToList();
            if (docs.GameplaySliceJson == null) return "slice text mirror missing";
            GdDict text = GdJson.ParseDict(docs.GameplaySliceJson);
            var textIds = text.GetArrayOrEmpty("loot_containers").OfType<GdDict>().Select(c => V.Str(c.Get("id", ""))).ToList();
            if (!ids.SequenceEqual(textIds)) return "slice text mirror disagrees on the containers";

            // appended after the existing containers, exactly once each
            int firstGuarantee = ids.FindIndex(id => Array.IndexOf(ContainerIds, id) >= 0);
            if (firstGuarantee < 0) return "guarantee containers missing";
            for (int i = 0; i < ids.Count; i++)
                if ((Array.IndexOf(ContainerIds, ids[i]) >= 0) != (i >= firstGuarantee)) return "guarantee containers are not appended after the existing ones";
            foreach (string id in ContainerIds)
                if (ids.Count(x => x == id) != 1) return "container " + id + " appears " + ids.Count(x => x == id) + " times";

            string start = V.Str(slice.Get("start_room", ""));
            string dock = HomeDockPlanner.DockRoomId(docs.Layout);
            var rooms = new HashSet<string>(StringComparer.Ordinal);
            var found = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (GdDict container in containers.OfType<GdDict>())
            {
                string id = V.Str(container.Get("id", ""));
                if (Array.IndexOf(ContainerIds, id) < 0) continue;
                string roomId = V.Str(container.Get("room_id", ""));
                if (roomId.Length == 0 || roomId == start || roomId == dock) return id + " sits in the start or dock room";
                if (!rooms.Add(roomId)) return id + " shares a room with another guarantee container";
                GdDict room = layoutRoom(docs.Layout, roomId);
                if (room.IsEmpty || !FirstRunAwayGate.CellInSlots(room, LayoutSerializer.ParseSlotCell(container.Get("approach_cell", new GdArray()))))
                    return id + " is not on a loot slot";
                if (!Reaches(docs.Layout, start, roomId)) return "unreachable:" + roomId;
                if (!Reaches(docs.Layout, roomId, dock)) return "unreachable:" + roomId + " (to the dock)";
                double kg = 0;
                foreach (GdDict stack in container.GetArrayOrEmpty("contents").OfType<GdDict>())
                {
                    string item = V.Str(stack.Get("item_id", ""));
                    long qty = V.I64(stack.Get("qty", 0L));
                    found[item] = (found.TryGetValue(item, out long n) ? n : 0) + qty;
                    GdDict def = ItemDefs.GetDefinition(ItemDefs.LoadDefinitions(), item);
                    kg += qty * def.GetFloat("weight");
                }
                if (kg > (spec ?? new Spec()).MaxContainerKg + 1e-9) return ReasonOverweight + ":" + id;
            }

            // everything the plan needs is there
            foreach (KeyValuePair<string, long> need in plan.Totals())
                if ((found.TryGetValue(need.Key, out long have) ? have : 0) < need.Value) return "missing " + need.Key + " (" + (found.TryGetValue(need.Key, out long h) ? h : 0) + " of " + need.Value + ")";

            // calm
            foreach (GdDict layout in new[] { docs.Layout, docs.SourceLayout }.Where(l => l != null))
            {
                if (!layout.GetArrayOrEmpty("encounters").IsEmpty) return "encounters remain";
                if (!layout.GetArrayOrEmpty("arc_zones").IsEmpty || !layout.GetArrayOrEmpty("fire_zones").IsEmpty || !layout.GetArrayOrEmpty("breach_zones").IsEmpty) return "hazard zones remain";
                foreach (GdDict room in layout.GetArrayOrEmpty("rooms").OfType<GdDict>())
                    if (V.Str(room.Get("variant", "standard")) != RoomVariantSelector.VARIANT_STANDARD) return "room variant " + V.Str(room.Get("variant", "")) + " remains";
            }
            if (!slice.GetArrayOrEmpty("arc_zones").IsEmpty || !slice.GetArrayOrEmpty("fire_zones").IsEmpty || !slice.GetArrayOrEmpty("breach_zones").IsEmpty) return "slice hazard zones remain";
            return HazardEdgeReason(docs.Layout);
        }

        static GdDict layoutRoom(GdDict layout, string roomId)
        {
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
                if (roomV is GdDict room && V.Str(room.Get("id", "")) == roomId) return room;
            return new GdDict();
        }

        /// <summary>Plain-text summary of a plan, for logs and reports.</summary>
        public static string Describe(Plan plan) =>
            plan == null ? "no plan"
            : (plan.Ok ? "ok" : plan.Reason) + " broken=[" + string.Join(",", plan.BrokenSubs) + "] kit=" + plan.RepairKitKg.ToString("0.0", CultureInfo.InvariantCulture)
              + "kg stores=" + plan.StoresKg.ToString("0.0", CultureInfo.InvariantCulture) + "kg rations=" + plan.Rations + " water=" + plan.Water
              + " margin=" + plan.CapacityMarginKg.ToString("0", CultureInfo.InvariantCulture) + "kg";
    }
}
