// Ported from scripts/procgen/layout_mutator.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// PKG-D5.4: pure zone/branch/wreck mutators for procgen layouts. Operates on <see cref="TopologyTemplate"/>
    /// clones and layout.json dictionaries. Wreck pre-applies module damage into <c>layout.module_damage</c> and a
    /// <see cref="ModuleIntegrityMap"/>.
    /// </summary>
    public static class LayoutMutator
    {
        public static readonly IReadOnlyList<string> WALL_PREFIXES = new[] { "wall_", "bulkhead_", "doorway_", "pillar_" };

        static readonly Vec2i Sentinel = new Vec2i(-99999, -99999);

        static GodotRandom SeededRng(long seedValue, long salt)
        {
            var rng = new GodotRandom();
            rng.Seed = (seedValue ^ salt) & 0x7FFFFFFF;
            if (rng.Seed == 0) rng.Seed = 1;
            return rng;
        }

        static string HopKey(string a, string b) => a + "|" + b;

        /// <summary>
        /// Overlay branch locks: copy non-critical hops into blocked_links and leave every room_link in place.
        /// Live generation uses this so room-link BFS stays true.
        /// </summary>
        public static long ApplyBranchOverlays(GdDict layout, long seedValue) => ApplyBranchMutators(layout, seedValue, true);

        /// <summary>
        /// Branch mutator. Legacy (overlay=false) removes blocked hops from room_links; overlay mode keeps room_links
        /// and stamps blocked_links copies. Cap is links.size()/4. Overlay protects every critical_path hop; legacy
        /// protects only the first hop.
        /// </summary>
        public static long ApplyBranchMutators(GdDict layout, long seedValue, bool overlay = false)
        {
            if (layout == null || layout.IsEmpty) return 0;
            GodotRandom rng = SeededRng(seedValue, 0xB1A4C4);
            if (!(layout.Get("room_links", new GdArray()) is GdArray linksSource)) return 0;
            GdArray links = linksSource.DeepCopy();
            if (links.Count < 3) return 0;
            HashSet<string> protectedHops = ProtectedHops(layout, overlay);
            var blocked = new GdArray();
            if (layout.Get("blocked_links", new GdArray()) is GdArray bv) blocked = bv.DeepCopy();
            var kept = new GdArray();
            long blockedN = 0;
            long maxBlock = System.Math.Max(1L, links.Count / 4);
            GdDict overlayFallback = new GdDict();
            var blockedKeys = new HashSet<string>();
            foreach (var existing in blocked)
            {
                if (!(existing is GdDict e)) continue;
                string ea = V.Str(e.Get("from_room", e.Get("from", "")));
                string eb = V.Str(e.Get("to_room", e.Get("to", "")));
                blockedKeys.Add(HopKey(ea, eb));
                blockedKeys.Add(HopKey(eb, ea));
            }
            foreach (var link in links)
            {
                if (!(link is GdDict linkDict)) continue;
                GdDict original = linkDict.DeepCopy();
                string a = V.Str(original.Get("from_room", original.Get("from", "")));
                string b = V.Str(original.Get("to_room", original.Get("to", "")));
                string key = HopKey(a, b);
                string keyR = HopKey(b, a);
                bool vertical = LinkIsVertical(original);
                bool canBlock = !protectedHops.Contains(key) && !protectedHops.Contains(keyR) && !vertical &&
                                !LinkIsBridge(links, blockedKeys, a, b);
                if (overlay)
                {
                    kept.Append(original);
                    if (canBlock)
                    {
                        if (overlayFallback.IsEmpty) overlayFallback = original;
                        if (blockedN < maxBlock && rng.Randf() < 0.28)
                        {
                            GdDict stamped = original.DeepCopy();
                            stamped["module_id"] = "doorway_frame_blocked_1x1";
                            stamped["reason"] = "branch_mutator";
                            blocked.Append(stamped);
                            blockedN += 1;
                            blockedKeys.Add(key);
                            blockedKeys.Add(keyR);
                        }
                    }
                    continue;
                }
                if (!canBlock)
                {
                    kept.Append(original);
                    continue;
                }
                if (blockedN < maxBlock && rng.Randf() < 0.28)
                {
                    original["module_id"] = "doorway_frame_blocked_1x1";
                    original["reason"] = "branch_mutator";
                    blocked.Append(original);
                    blockedN += 1;
                    blockedKeys.Add(key);
                    blockedKeys.Add(keyR);
                }
                else
                {
                    kept.Append(original);
                }
            }
            if (overlay && blockedN == 0 && !overlayFallback.IsEmpty)
            {
                GdDict forced = overlayFallback.DeepCopy();
                forced["module_id"] = "doorway_frame_blocked_1x1";
                forced["reason"] = "branch_mutator";
                blocked.Append(forced);
                blockedN = 1;
            }
            layout["room_links"] = kept;
            layout["blocked_links"] = blocked;
            return blockedN;
        }

        /// <summary>
        /// Stamps matching portals[].state from blocked_links (LOCKED) and optionally converts one non-critical
        /// remaining DOOR to BREACH. Never inserts a portal.
        /// </summary>
        public static long ApplyPortalOverlays(GdDict layout, long seedValue, bool allowBreach = false)
        {
            if (layout == null || layout.IsEmpty) return 0;
            if (!(layout.Get("portals", new GdArray()) is GdArray portals)) return 0;
            long stamped = 0;
            GdArray blocked = layout.Get("blocked_links", new GdArray()) as GdArray ?? new GdArray();
            for (int i = 0; i < portals.Count; i++)
            {
                if (!(portals[i] is GdDict portal)) continue;
                if (!PortalMatchesAnyLink(portal, blocked)) continue;
                portal["state"] = "LOCKED";
                portal["module_id"] = "doorway_frame_blocked_1x1";
                stamped += 1;
            }
            if (allowBreach) stamped += StampOneBreachPortal(layout, seedValue);
            layout["portals"] = portals;
            return stamped;
        }

        /// <summary>Wreck stamp keyed by loader module_key after compile. Does not restamp.</summary>
        public static long ApplyWreckToCompiledPlan(GdDict layout, long seedValue, ModuleIntegrityMap integrityMap = null, double damageFraction = 0.35)
        {
            if (layout == null || layout.IsEmpty) return 0;
            if (V.Bool(layout.Get("wreck_applied", false)))
            {
                object existing = layout.Get("module_damage", new GdArray());
                return existing is GdArray existingArr ? existingArr.Count : 0;
            }
            if (!(layout.Get("structural_plan", new GdDict()) is GdDict plan) || plan.IsEmpty) return 0;
            GodotRandom rng = SeededRng(seedValue, 0x1EC4001);
            double frac = GdMath.Clampf(damageFraction, 0.05, 0.9);
            ModuleIntegrityMap map = integrityMap ?? new ModuleIntegrityMap();
            var candidates = new GdArray();
            CollectCompiledRecords(candidates, plan.Get("floor_placements", new GdArray()), "floor");
            CollectCompiledRecords(candidates, plan.Get("placements", new GdArray()), "edge");
            CollectCompiledRecords(candidates, plan.Get("ceiling_placements", new GdArray()), "ceiling");
            var damages = new GdArray();
            long damaged = 0;
            GdDict fallback = new GdDict();
            foreach (var recVariant in candidates)
            {
                if (!(recVariant is GdDict rec)) continue;
                if (rng.Randf() > frac)
                {
                    if (fallback.IsEmpty) fallback = rec;
                    continue;
                }
                StampCompiledDamage(rec, map, rng, damages);
                damaged += 1;
            }
            if (damaged == 0 && !fallback.IsEmpty)
            {
                StampCompiledDamage(fallback, map, rng, damages);
                damaged = 1;
            }
            layout["module_damage"] = damages;
            layout["wreck_applied"] = true;
            layout["wreck_seed"] = seedValue;
            return damaged;
        }

        static bool IsStructural(string moduleKind)
        {
            if (string.IsNullOrEmpty(moduleKind)) return false;
            foreach (string prefix in WALL_PREFIXES)
                if (GdString.BeginsWith(moduleKind, prefix)) return true;
            string k = moduleKind.ToLowerInvariant();
            return GdString.BeginsWith(k, "floor_") || GdString.BeginsWith(k, "corridor_") || GdString.BeginsWith(k, "ceiling_");
        }

        static HashSet<string> ProtectedHops(GdDict layout, bool overlay)
        {
            var protectedHops = new HashSet<string>();
            if (!(layout.Get("critical_path", new GdArray()) is GdArray path) || path.Count < 2) return protectedHops;
            int last = overlay ? path.Count - 1 : 1;
            last = System.Math.Min(last, path.Count - 1);
            for (int i = 0; i < last; i++)
            {
                string a = V.Str(path[i]);
                string b = V.Str(path[i + 1]);
                protectedHops.Add(HopKey(a, b));
                protectedHops.Add(HopKey(b, a));
            }
            return protectedHops;
        }

        static bool LinkIsBridge(GdArray links, HashSet<string> blockedKeys, string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return true;
            var skip = new HashSet<string>(blockedKeys) { HopKey(a, b), HopKey(b, a) };
            return !RoomsReachable(links, skip, a, b);
        }

        static bool RoomsReachable(GdArray links, HashSet<string> skip, string startId, string goalId)
        {
            if (startId == goalId) return true;
            var adj = new GdDict();
            foreach (var linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                string fa = V.Str(link.Get("from_room", link.Get("from", "")));
                string tb = V.Str(link.Get("to_room", link.Get("to", "")));
                if (fa.Length == 0 || tb.Length == 0 || fa == tb) continue;
                if (skip.Contains(HopKey(fa, tb)) || skip.Contains(HopKey(tb, fa))) continue;
                if (!adj.Has(fa)) adj[fa] = new GdArray();
                if (!adj.Has(tb)) adj[tb] = new GdArray();
                ((GdArray)adj[fa]).Append(tb);
                ((GdArray)adj[tb]).Append(fa);
            }
            if (!adj.Has(startId)) return false;
            var seen = new HashSet<string> { startId };
            var queue = new List<string> { startId };
            int head = 0;
            while (head < queue.Count)
            {
                string cur = queue[head++];
                if (cur == goalId) return true;
                foreach (var nxtVariant in adj.GetArrayOrEmpty(cur))
                {
                    string nxt = V.Str(nxtVariant);
                    if (!seen.Add(nxt)) continue;
                    queue.Add(nxt);
                }
            }
            return false;
        }

        static bool LinkIsVertical(GdDict link)
        {
            if (link.Get("from_cell", null) is GdArray fa && link.Get("to_cell", null) is GdArray ta)
            {
                if (fa.Count >= 3 && ta.Count >= 3) return V.I64(fa[2]) != V.I64(ta[2]);
            }
            return false;
        }

        static bool PortalMatchesAnyLink(GdDict portal, GdArray links)
        {
            foreach (var linkVariant in links)
            {
                if (!(linkVariant is GdDict link)) continue;
                if (PortalMatchesLink(portal, link)) return true;
            }
            return false;
        }

        static bool PortalMatchesLink(GdDict portal, GdDict link)
        {
            string pf = V.Str(portal.Get("from_room", ""));
            string pt = V.Str(portal.Get("to_room", ""));
            string lf = V.Str(link.Get("from_room", link.Get("from", "")));
            string lt = V.Str(link.Get("to_room", link.Get("to", "")));
            if (pf.Length == 0 || pt.Length == 0 || lf.Length == 0 || lt.Length == 0) return false;
            bool roomsMatch = (pf == lf && pt == lt) || (pf == lt && pt == lf);
            if (!roomsMatch) return false;
            Vec2i pFrom = CellXz(portal.Get("from_cell", portal.Get("cell", null)));
            Vec2i pTo = CellXz(portal.Get("to_cell", null));
            Vec2i lFrom = CellXz(link.Get("from_cell", null));
            Vec2i lTo = CellXz(link.Get("to_cell", null));
            if (lFrom == Sentinel || lTo == Sentinel) return true;
            if (pFrom == Sentinel || pTo == Sentinel) return true;
            return (pFrom == lFrom && pTo == lTo) || (pFrom == lTo && pTo == lFrom);
        }

        static long StampOneBreachPortal(GdDict layout, long seedValue)
        {
            if (!(layout.Get("portals", new GdArray()) is GdArray portals)) return 0;
            HashSet<string> protectedHops = ProtectedHops(layout, true);
            GodotRandom rng = SeededRng(seedValue, 0xB4EAC4);
            GdArray links = layout.Get("room_links", new GdArray()) as GdArray ?? new GdArray();
            var blockedKeys = new HashSet<string>();
            if (layout.Get("blocked_links", new GdArray()) is GdArray blocked)
            {
                foreach (var existing in blocked)
                {
                    if (!(existing is GdDict e)) continue;
                    string ea = V.Str(e.Get("from_room", e.Get("from", "")));
                    string eb = V.Str(e.Get("to_room", e.Get("to", "")));
                    blockedKeys.Add(HopKey(ea, eb));
                    blockedKeys.Add(HopKey(eb, ea));
                }
            }
            var candidates = new List<int>();
            for (int i = 0; i < portals.Count; i++)
            {
                if (!(portals[i] is GdDict portal)) continue;
                string state = V.Str(portal.Get("state", "DOOR")).ToUpperInvariant();
                if (state != "DOOR") continue;
                string a = V.Str(portal.Get("from_room", ""));
                string b = V.Str(portal.Get("to_room", ""));
                if (protectedHops.Contains(HopKey(a, b)) || protectedHops.Contains(HopKey(b, a))) continue;
                if (LinkIsBridge(links, blockedKeys, a, b)) continue;
                candidates.Add(i);
            }
            if (candidates.Count == 0) return 0;
            int pick = candidates[(int)rng.RandiRange(0, candidates.Count - 1)];
            var chosen = (GdDict)portals[pick];
            chosen["state"] = "BREACH";
            chosen["module_id"] = "";
            layout["portals"] = portals;
            return 1;
        }

        static Vec2i CellXz(object value)
        {
            if (value is Vec2i v) return v;
            if (value is GdArray arr && arr.Count >= 2) return new Vec2i(V.I32(arr[0]), V.I32(arr[1]));
            return Sentinel;
        }

        static void CollectCompiledRecords(GdArray output, object recordsVariant, string layer)
        {
            if (!(recordsVariant is GdArray records)) return;
            foreach (var recVariant in records)
            {
                if (!(recVariant is GdDict rec)) continue;
                string moduleKind = V.Str(rec.Get("module_id", rec.Get("module", "")));
                if (!IsStructural(moduleKind)) continue;
                string keyPart = layer == "edge"
                    ? V.Str(rec.Get("edge_key", rec.Get("key", "")))
                    : V.Str(rec.Get("cell_key", ""));
                if (keyPart.Length == 0) continue;
                string roomId = V.Str(rec.Get("room_id", ""));
                if (roomId.Length == 0)
                {
                    if (rec.Get("room_ids", new GdArray()) is GdArray roomIds && !roomIds.IsEmpty) roomId = V.Str(roomIds[0]);
                }
                output.Append(new GdDict
                {
                    { "layer", layer },
                    { "key_part", keyPart },
                    { "kind", moduleKind },
                    { "room_id", roomId },
                    { "placement_id", V.Str(rec.Get("placement_id", rec.Get("id", ""))) },
                });
            }
        }

        static void StampCompiledDamage(GdDict rec, ModuleIntegrityMap map, GodotRandom rng, GdArray damages)
        {
            double amount = 0.30 + rng.Randf() * 0.38;
            string moduleKey = V.Str(rec.Get("layer", "edge")) + "/" + V.Str(rec.Get("key_part", ""));
            string moduleKind = V.Str(rec.Get("kind", ""));
            string roomId = V.Str(rec.Get("room_id", ""));
            map.EnsureModule(moduleKey, moduleKind, new GdDict(), roomId);
            string state = map.ApplyDamage(moduleKey, amount, moduleKind);
            damages.Append(new GdDict
            {
                { "module_id", moduleKey },
                { "module_key", moduleKey },
                { "placement_id", V.Str(rec.Get("placement_id", "")) },
                { "kind", moduleKind },
                { "room_id", roomId },
                { "amount", amount },
                { "state", state },
            });
        }

    }
}
