// Ported from scripts/systems/spatial_perception_state.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-C4.1a: pure spatial perception over room adjacency + door state. Occlusion and noise muffling
    /// without physics raycasts.
    /// </summary>
    public sealed class SpatialPerceptionState
    {
        /// <summary>Per open portal hop.</summary>
        public const double OPEN_NOISE_ATTEN = 0.85;
        /// <summary>Closed hatch heavily muffles.</summary>
        public const double CLOSED_NOISE_ATTEN = 0.12;
        /// <summary>Sealed / biomatter block.</summary>
        public const double BLOCKED_NOISE_ATTEN = 0.05;
        public const long MAX_HOPS = 8;

        /// <summary>room_id -> true.</summary>
        public GdDict Rooms = new GdDict();
        /// <summary>Undirected link key "a|b" -> { from, to, link_id, kind: open|closed|blocked, module_id }.</summary>
        public GdDict Links = new GdDict();
        /// <summary>Link key -> "open" | "closed" | "blocked".</summary>
        public GdDict DoorStates = new GdDict();

        public void Clear()
        {
            Rooms.Clear();
            Links.Clear();
            DoorStates.Clear();
        }

        /// <summary>Builds from a layout.json-shaped dictionary (rooms, room_links, blocked_links). Returns the link count ingested.</summary>
        public long ConfigureFromLayout(GdDict layout)
        {
            Clear();
            object roomsV = layout.Get("rooms", new GdArray());
            if (roomsV is GdArray rooms)
            {
                foreach (object r in rooms)
                {
                    if (!(r is GdDict rd)) continue;
                    string rid = V.Str(rd.Get("id", ""));
                    if (rid.Length != 0) Rooms[rid] = true;
                }
            }
            long openN = IngestLinks(layout.Get("room_links", new GdArray()), "open");
            long blockedN = IngestLinks(layout.Get("blocked_links", new GdArray()), "blocked");
            return openN + blockedN;
        }

        long IngestLinks(object raw, string defaultKind)
        {
            if (!(raw is GdArray items)) return 0;
            long n = 0;
            foreach (object item in items)
            {
                if (!(item is GdDict d)) continue;
                string a = V.Str(d.Get("from_room", d.Get("from", "")));
                string b = V.Str(d.Get("to_room", d.Get("to", "")));
                if (a.Length == 0 || b.Length == 0 || a == b) continue;
                Rooms[a] = true;
                Rooms[b] = true;
                string key = LinkKey(a, b);
                string moduleId = V.Str(d.Get("module_id", ""));
                string kind = defaultKind;
                if (SurvivalCompat.Contains(moduleId, "blocked") || SurvivalCompat.Contains(moduleId, "sealed"))
                {
                    kind = "blocked";
                }
                else if (SurvivalCompat.Contains(moduleId, "closed") || SurvivalCompat.Contains(moduleId, "hatch_closed"))
                {
                    kind = "closed";
                }
                else if (SurvivalCompat.Contains(moduleId, "open") || SurvivalCompat.Contains(moduleId, "doorway") ||
                         SurvivalCompat.Contains(moduleId, "ramp"))
                {
                    if (defaultKind != "blocked") kind = "open";
                }
                string linkId = V.Str(d.Get("id", key));
                Links[key] = new GdDict
                {
                    { "from", a },
                    { "to", b },
                    { "link_id", linkId },
                    { "kind", kind },
                    { "module_id", moduleId },
                };
                // Initial door state from kind
                if (kind == "open")
                    DoorStates[key] = "open";
                else if (kind == "closed")
                    DoorStates[key] = "closed";
                else
                    DoorStates[key] = "blocked";
                n += 1;
            }
            return n;
        }

        static string LinkKey(string a, string b)
        {
            if (SurvivalCompat.Less(a, b)) return a + "|" + b;
            return b + "|" + a;
        }

        public bool SetDoorState(string roomA, string roomB, string state)
        {
            string key = LinkKey(roomA, roomB);
            if (!Links.Has(key)) return false;
            string s = state;
            if (s != "open" && s != "closed" && s != "blocked") return false;
            // Cannot open a permanently blocked link without unblocking first
            string kind = V.Str(((GdDict)Links[key]).Get("kind", "open"));
            if (kind == "blocked" && s == "open") return false;
            DoorStates[key] = s;
            return true;
        }

        public bool UnblockLink(string roomA, string roomB)
        {
            string key = LinkKey(roomA, roomB);
            if (!Links.Has(key)) return false;
            var link = (GdDict)Links[key];
            link["kind"] = "open";
            Links[key] = link;
            DoorStates[key] = "open";
            return true;
        }

        public string GetDoorState(string roomA, string roomB)
        {
            string key = LinkKey(roomA, roomB);
            return V.Str(DoorStates.Get(key, ""));
        }

        public bool HasRoom(string roomId) => Rooms.Has(roomId);

        public long LinkCount() => Links.Count;

        /// <summary>Sight: same room, or a path of only open doors.</summary>
        public bool CanSee(string fromRoom, string toRoom)
        {
            if (fromRoom.Length == 0 || toRoom.Length == 0) return false;
            if (fromRoom == toRoom) return true;
            return PathExists(fromRoom, toRoom, true);
        }

        /// <summary>Noise remaining after attenuation along the best path (0..source).</summary>
        public double AttenuateNoise(string fromRoom, string toRoom, double sourceNoise)
        {
            if (sourceNoise <= 0.0) return 0.0;
            if (fromRoom.Length == 0 || toRoom.Length == 0) return 0.0;
            if (fromRoom == toRoom) return sourceNoise;
            double pathAtten = BestNoiseAttenuation(fromRoom, toRoom);
            return sourceNoise * pathAtten;
        }

        /// <summary>True if any noise above threshold could be heard.</summary>
        public bool CanHear(string fromRoom, string toRoom, double sourceNoise, double threshold = 0.05) =>
            AttenuateNoise(fromRoom, toRoom, sourceNoise) >= threshold;

        bool EdgeAllowsSight(string key)
        {
            string st = V.Str(DoorStates.Get(key, "closed"));
            return st == "open";
        }

        double EdgeNoiseMult(string key)
        {
            string st = V.Str(DoorStates.Get(key, "closed"));
            switch (st)
            {
                case "open": return OPEN_NOISE_ATTEN;
                case "closed": return CLOSED_NOISE_ATTEN;
                case "blocked": return BLOCKED_NOISE_ATTEN;
                default: return CLOSED_NOISE_ATTEN;
            }
        }

        GdArray Neighbors(string roomId)
        {
            var output = new GdArray();
            foreach (var kv in Links)
            {
                var link = (GdDict)kv.Value;
                string a = V.Str(link.Get("from", ""));
                string b = V.Str(link.Get("to", ""));
                if (a == roomId)
                    output.Append(new GdDict { { "room", b }, { "key", V.Str(kv.Key) } });
                else if (b == roomId)
                    output.Append(new GdDict { { "room", a }, { "key", V.Str(kv.Key) } });
            }
            return output;
        }

        bool PathExists(string fromRoom, string toRoom, bool sightOnly)
        {
            if (!Rooms.Has(fromRoom) || !Rooms.Has(toRoom)) return false;
            var visited = new GdDict { { fromRoom, true } };
            var queue = GdArray.Of(fromRoom);
            long hops = 0;
            while (!queue.IsEmpty && hops < MAX_HOPS * Rooms.Count)
            {
                string cur = V.Str(queue.PopFront());
                hops += 1;
                if (cur == toRoom) return true;
                foreach (object nbVariant in Neighbors(cur))
                {
                    var nb = (GdDict)nbVariant;
                    string nroom = V.Str(nb.Get("room", ""));
                    string key = V.Str(nb.Get("key", ""));
                    if (visited.Has(nroom)) continue;
                    if (sightOnly && !EdgeAllowsSight(key)) continue;
                    // Noise paths always traverse (they attenuate instead).
                    visited[nroom] = true;
                    queue.Append(nroom);
                }
            }
            return visited.Has(toRoom);
        }

        /// <summary>Bellman-Ford style relaxation of the max attenuation product (best remaining noise fraction).</summary>
        double BestNoiseAttenuation(string fromRoom, string toRoom)
        {
            if (!Rooms.Has(fromRoom) || !Rooms.Has(toRoom)) return 0.0;
            var best = new GdDict(); // room -> atten product
            foreach (object r in Rooms.Keys) best[V.Str(r)] = 0.0;
            best[fromRoom] = 1.0;
            int passes = Math.Max(1, Rooms.Count);
            for (int pass = 0; pass < passes; pass++)
            {
                bool changed = false;
                foreach (var kv in Links)
                {
                    var link = (GdDict)kv.Value;
                    string a = V.Str(link.Get("from", ""));
                    string b = V.Str(link.Get("to", ""));
                    double mult = EdgeNoiseMult(V.Str(kv.Key));
                    double viaA = V.F64(best.Get(a, 0.0)) * mult;
                    if (viaA > V.F64(best.Get(b, 0.0)))
                    {
                        best[b] = viaA;
                        changed = true;
                    }
                    double viaB = V.F64(best.Get(b, 0.0)) * mult;
                    if (viaB > V.F64(best.Get(a, 0.0)))
                    {
                        best[a] = viaB;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
            return V.F64(best.Get(toRoom, 0.0));
        }

        /// <summary>Convenience perception probe for the threat AI.</summary>
        public GdDict Probe(string observerRoom, string targetRoom, double emittedNoise, double emittedVisibility = 1.0)
        {
            bool seen = CanSee(observerRoom, targetRoom);
            double heardLevel = AttenuateNoise(targetRoom, observerRoom, emittedNoise);
            return new GdDict
            {
                { "observer_room", observerRoom },
                { "target_room", targetRoom },
                { "seen", seen },
                { "heard", heardLevel >= 0.05 },
                { "noise_at_observer", heardLevel },
                { "visibility", seen ? emittedVisibility : 0.0 },
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", "spatial_perception_v1" },
                { "room_count", (long)Rooms.Count },
                { "link_count", (long)Links.Count },
                { "links", Links.DeepCopy() },
                { "door_states", DoorStates.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            Clear();
            object r = summary.Get("links", new GdDict());
            if (r is GdDict links)
            {
                Links = links.DeepCopy();
                foreach (var kv in Links)
                {
                    var link = (GdDict)kv.Value;
                    Rooms[V.Str(link.Get("from", ""))] = true;
                    Rooms[V.Str(link.Get("to", ""))] = true;
                }
            }
            object d = summary.Get("door_states", new GdDict());
            if (d is GdDict doorStates) DoorStates = doorStates.DeepCopy();
            return true;
        }
    }
}
