using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>New ships only: immutable v2 profile, a cargo exchange or the established service hull.</summary>
    public static class PurposefulExpedition
    {
        public const string Profile = "purposeful_expedition_v2";
        public static bool Supported(string profile) => profile == Profile || profile == ExpeditionLayoutEngine.Profile || profile == ConstrainedExpedition.Profile;
        public static bool CrossHull(long seed) => (seed & 1) != 0;

        public static void StampDockContract(GdDict layout)
        {
            if(layout.GetString("generation_profile")!=Profile && layout.GetString("generation_profile")!=ConstrainedExpedition.Profile) return;
            var rooms=layout.GetArrayOrEmpty("rooms"); GdDict dock=null;
            foreach(GdDict room in rooms) if(room.GetString("room_role")=="dock") {dock=room;break;}
            if(dock==null) return;
            int outwardX=-1,outwardZ=0;
            foreach(GdDict portal in layout.GetArrayOrEmpty("portals"))
            {
                bool from=portal.GetString("from_room")==dock.GetString("id");
                bool to=portal.GetString("to_room")==dock.GetString("id"); if(!from&&!to) continue;
                var a=LayoutSerializer.ParseSlotCell(portal.Get(from?"from_cell":"to_cell")); var b=LayoutSerializer.ParseSlotCell(portal.Get(from?"to_cell":"from_cell"));
                if(a.Count<2||b.Count<2) continue;
                outwardX=(int)(V.I64(a[0])-V.I64(b[0])); outwardZ=(int)(V.I64(a[1])-V.I64(b[1])); break;
            }
            GdArray chosen=null; long best=long.MinValue;
            foreach(object value in dock.GetArrayOrEmpty("cells"))
            {
                var cell=LayoutSerializer.ParseSlotCell(value); if(cell.Count<2) continue;
                long projection=V.I64(cell[0])*outwardX+V.I64(cell[1])*outwardZ;
                if(projection>best) {best=projection;chosen=cell;}
            }
            if(chosen==null) return;
            // A real outer dock cell aligns the overlapping boat with one complete wall/floor span.
            // Averaging a 2x2 room places it halfway across two wall modules and traps the player.
            layout["docking_port"]=new GdDict {{"contract_version",1L},{"room_id",dock.GetString("id")},{"cell",chosen.DeepCopy()},
                {"position",GdArray.Of(V.I64(chosen[0])*4.0,dock.GetInt("deck")*4.0,V.I64(chosen[1])*4.0)},
                {"facing",GdArray.Of((double)outwardX,0.0,(double)outwardZ)}};
        }

        public static GdDict Layout(ShipBlueprint blueprint, out List<GdDict> plan)
        {
            if (!CrossHull(blueprint.SeedValue)) return ExpeditionLayoutEngine.Layout(blueprint, out plan);
            var rng = GodotRandom.FromSeed(blueprint.SeedValue);
            int width = (int)(blueprint.ShipSize == 1 ? rng.RandiRange(4, 5) : rng.RandiRange(5, 6)), arm = width / 2 - 1;
            int quarter = (int)rng.RandiRange(0, 3);
            var rooms = new GdDict(); var owners = new Dictionary<Vec2i, string>(); var order = new List<Vec2i>();
            var counts = new Dictionary<string, int>(); var roomPlan = new List<GdDict>();
            void Add(string role, int x, int y, int w, int h)
            {
                counts.TryGetValue(role, out int count); counts[role] = ++count; string id = role + "_" + count.ToString("D2");
                var cells = new GdArray();
                for (int cy = y; cy < y + h; cy++) for (int cx = x; cx < x + w; cx++)
                {
                    var cell = new Vec2i(cx, cy); for (int q = 0; q < quarter; q++) cell = new Vec2i(-cell.Y, cell.X);
                    owners.Add(cell, id); order.Add(cell); cells.Append(cell);
                }
                var footprint = quarter % 2 == 0 ? new Vec2i(w, h) : new Vec2i(h, w);
                rooms[id] = new GdDict { { "cells", cells }, { "origin", cells[0] }, { "footprint", footprint }, { "deck", 0L }, { "role", role } };
                roomPlan.Add(new GdDict { { "id", id }, { "role", role }, { "variant", "standard" }, { "deck", 0L }, { "footprint", footprint }, { "target_cells", (long)w * h } });
            }
            Add("dock", -5, arm, 2, 2);
            Add("cargo", 0, 0, width, width);
            Add("corridor", -3, arm, 3, 2); Add("corridor", width, arm, 3, 2);
            Add("corridor", arm, -3, 2, 3); Add("corridor", arm, width, 2, 3);
            Add("medical", -3, -3, 3, arm + 3);
            Add("crew_quarters", -3, arm + 2, 3, width + 1 - arm);
            Add("bridge", arm + 2, -3, width + 1 - arm, 3);
            Add("engineering", width, arm + 2, 3, width + 1 - arm);
            Add("maintenance", arm, width + 3, 2, 2);
            var links = new GdArray(); var pairs = new HashSet<string>();
            foreach (var cell in order) foreach (var dir in CellLayoutEngine.ALL_DIRS)
            {
                var next = new Vec2i(cell.X + dir.X, cell.Y + dir.Y);
                if (!owners.TryGetValue(next, out string other) || owners[cell] == other) continue;
                string from = owners[cell], key = string.CompareOrdinal(from, other) < 0 ? from + "|" + other : other + "|" + from;
                if (pairs.Add(key)) links.Append(new GdDict { { "from_room", from }, { "to_room", other }, { "from_cell", cell }, { "to_cell", next } });
            }
            plan = roomPlan; return new GdDict { { "rooms", rooms }, { "adjacencies", links } };
        }

        public static void Furnish(GdDict layout, GdDict gameplay)
        {
            if (layout.GetString("generation_profile") != Profile && layout.GetString("generation_profile") != ConstrainedExpedition.Profile) return;
            var reserved = new HashSet<string>();
            string Key(GdArray cell) => cell.Count >= 2 ? V.I64(cell[0]) + ":" + V.I64(cell[1]) : "";
            foreach (GdDict portal in layout.GetArrayOrEmpty("portals"))
                foreach (string side in new[] { "from_cell", "to_cell" }) reserved.Add(Key(LayoutSerializer.ParseSlotCell(portal.Get(side))));
            foreach (string category in new[] { "objectives", "loot_containers" })
                foreach (GdDict item in gameplay.GetArrayOrEmpty(category)) reserved.Add(Key(item.GetArrayOrEmpty("approach_cell")));
            var placements = new GdArray();
            foreach (GdDict room in layout.GetArrayOrEmpty("rooms"))
            {
                string role = room.GetString("room_role"); string[] assets;
                switch (role)
                {
                    case "cargo": assets = new[] { "cargo_pallet", "generic_crate", "salvage_cart", "cargo_pallet", "generic_crate", "generic_crate", "cargo_pallet", "salvage_cart" }; break;
                    case "medical": assets = new[] { "exam_cot", "medical_cabinet", "exam_cot", "medical_cabinet", "focused_work_lamp", "generic_locker" }; break;
                    case "crew_quarters": assets = new[] { "sleep_berth", "generic_locker", "sleep_berth", "generic_locker", "sleep_berth", "generic_locker", "sleep_berth", "generic_locker" }; break;
                    case "engineering": case "maintenance": assets = new[] { "maintenance_bench", "service_rack", "cable_tray", "focused_work_lamp", "generic_locker" }; break;
                    case "bridge": assets = new[] { "service_rack", "generic_locker" }; break;
                    default: continue;
                }
                var cells = new GdArray();
                foreach (object value in room.GetArrayOrEmpty("cells"))
                {
                    var parsed = LayoutSerializer.ParseSlotCell(value); if (parsed.Count >= 2) cells.Append(parsed);
                }
                var owned = new HashSet<string>(); foreach (GdArray cell in cells) owned.Add(Key(cell));
                int count = 0; var candidates = new List<(GdArray cell,int edge)>();
                // Perimeter only; cell centers, doors and existing objective/loot anchors remain clear.
                foreach (GdArray cell in cells)
                {
                    if (reserved.Contains(Key(cell))) continue;
                    long x = V.I64(cell[0]), y = V.I64(cell[1]); int edge = -1;
                    var dx = new[] { -1, 0, 1, 0 }; var dy = new[] { 0, -1, 0, 1 };
                    for (int d = 0; d < 4; d++) if (!owned.Contains((x + dx[d]) + ":" + (y + dy[d]))) { edge = d; break; }
                    if (edge < 0) continue;
                    candidates.Add((cell,edge));
                }
                int budget = System.Math.Min(assets.Length,candidates.Count);
                for(int slot=0;slot<budget;slot++)
                {
                    var candidate=candidates[slot*candidates.Count/budget]; var cell=candidate.cell; int edge=candidate.edge;
                    var dx = new[] { -1, 0, 1, 0 }; var dy = new[] { 0, -1, 0, 1 };
                    placements.Append(new GdDict { { "id", "interior_" + room.GetString("id") + "_" + count }, { "room_id", room.GetString("id") },
                        { "role", role }, { "asset_id", assets[count++] }, { "cell", cell.DeepCopy() }, { "deck", room.GetInt("deck") },
                        { "offset_x", dx[edge] * 1.2 }, { "offset_z", dy[edge] * 1.2 }, { "yaw", (long)(edge * 90) } });
                }
            }
            layout["purposeful_interiors"] = placements;
        }
    }
}
