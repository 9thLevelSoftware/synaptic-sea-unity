// Ported from scripts/systems/component_placement_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.3a pure placement of components into wall/center slots. Deterministic under (layout, seed); never
    /// touches the scene tree. <see cref="Placed"/> entries: <c>{component_instance_id, component_id, room_id,
    /// slot_kind, slot_index, cell, against_wall, condition, item_form, mass, linked_system, linked_subcomponent,
    /// mounted}</c>.
    /// </summary>
    public class ComponentPlacementState : ComponentMountResolver.IComponentPlacement, IComponentManifestModel
    {
        public const long MAX_WALL_FILLS = 3;
        public const long MAX_CENTER_FILLS = 1;

        public GdArray Placed = new GdArray();
        public long SeedValue = 0;

        public void Clear() => Placed.Clear();

        /// <summary>Fills wall/center slots of every room from the catalog role sets; returns the placed count.</summary>
        public long Populate(GdDict layout, ComponentCatalog catalog, long pSeed, GdDict occupiedCells = null)
        {
            Clear();
            SeedValue = pSeed;
            if (catalog == null) return 0;
            if (!(layout.Get("rooms", new GdArray()) is GdArray rooms)) return 0;
            var rng = new GodotRandom();
            rng.Seed = (pSeed ^ 0xC0A1E5C) & 0x7FFFFFFF;
            if (rng.Seed == 0) rng.Seed = 1;
            var usedKeys = new GdDict(); // room|slot_kind|index -> true
            GdDict usedCells = (occupiedCells ?? new GdDict()).ShallowCopy();
            long instanceN = 0;
            foreach (var roomVariant in rooms)
            {
                if (!(roomVariant is GdDict room)) continue;
                string roomId = V.Str(room.Get("id", ""));
                if (roomId.Length == 0) continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "default")));
                instanceN += FillSlots(room, roomId, role, "wall", "wall_slots", catalog, rng, usedKeys, usedCells);
                instanceN += FillSlots(room, roomId, role, "center", "center_slots", catalog, rng, usedKeys, usedCells);
            }
            return Placed.Count;
        }

        long FillSlots(GdDict room, string roomId, string role, string slotKind, string slotKey, ComponentCatalog catalog,
            GodotRandom rng, GdDict usedKeys, GdDict usedCells)
        {
            GdArray slots = ExtractSlots(room, slotKey);
            if (slots.IsEmpty) return 0;
            GdArray choices = catalog.RoleSet(role, slotKind);
            if (choices.IsEmpty) return 0;
            GdDict reserved = ReservedCellKeys(room, roomId);
            long filled = 0;
            long maxFill = slotKind == "wall" ? MAX_WALL_FILLS : MAX_CENTER_FILLS;
            for (int i = 0; i < slots.Count; i++)
            {
                if (filled >= maxFill) break;
                string key = roomId + "|" + slotKind + "|" + GdString.FormatInt(i);
                if (usedKeys.Has(key)) continue;
                GdDict slotInfo = slots[i] as GdDict ?? new GdDict();
                object cellValue = slotInfo.Get("cell", "");
                GdArray parsedCell = LayoutSerializer.ParseSlotCell(cellValue);
                string cellKey = CellOccupancyKey(roomId, parsedCell);
                if (cellKey.Length != 0 && (usedCells.Has(cellKey) || reserved.Has(cellKey))) continue;
                string componentId = WeightedPick(choices, rng);
                if (componentId.Length == 0 || !catalog.HasComponent(componentId)) continue;
                GdDict def = catalog.GetComponent(componentId);
                // Prefer components whose slot matches.
                string wantSlot = V.Str(def.Get("slot", slotKind));
                if (wantSlot != slotKind && wantSlot != "any")
                {
                    // Try once more.
                    componentId = WeightedPick(choices, rng);
                    if (componentId.Length == 0) continue;
                    def = catalog.GetComponent(componentId);
                    wantSlot = V.Str(def.Get("slot", slotKind));
                    if (wantSlot != slotKind && wantSlot != "any") continue;
                }
                object storedCell = parsedCell.Count >= 2 ? parsedCell : cellValue;
                var entry = new GdDict
                {
                    { "component_instance_id", roomId + "_" + slotKind + "_" + GdString.FormatInt(i) },
                    { "component_id", componentId },
                    { "room_id", roomId },
                    { "slot_kind", slotKind },
                    { "slot_index", (long)i },
                    { "cell", storedCell },
                    { "against_wall", V.Bool(slotInfo.Get("against_wall", slotKind == "wall")) },
                    { "condition", V.F64(def.Get("condition_default", 1.0)) },
                    { "item_form", V.Str(def.Get("item_form", componentId)) },
                    { "mass", V.F64(def.Get("mass", 10.0)) },
                    { "linked_system", V.Str(def.Get("linked_system", "")) },
                    { "linked_subcomponent", V.Str(def.Get("linked_subcomponent", "")) },
                    { "mounted", true },
                };
                Placed.Append(entry);
                usedKeys[key] = true;
                if (cellKey.Length != 0) usedCells[cellKey] = true;
                filled += 1;
            }
            return filled;
        }

        GdArray ExtractSlots(GdDict room, string slotKey)
        {
            // REQ-FILL-001: interior_zones first; an all-empty zone object counts as absent.
            if (room.Get("interior_zones", null) is GdDict interior && InteriorZonesHaveSlots(interior))
            {
                if (interior.Get(slotKey, new GdArray()) is GdArray interiorSlots)
                    return NormalizeSlots(interiorSlots, slotKey == "wall_slots");
                return new GdArray();
            }
            // Legacy: slots may live on the room root or under zones.
            if (room.Get(slotKey, null) is GdArray direct && !direct.IsEmpty) return NormalizeSlots(direct, slotKey == "wall_slots");
            if (room.Get("zones", new GdDict()) is GdDict zones)
            {
                if (zones.Get(slotKey, new GdArray()) is GdArray z && !z.IsEmpty) return NormalizeSlots(z, slotKey == "wall_slots");
            }
            // Golden/hub layouts often only stamp floor structural_placements — synthesize slots from floor cells.
            return SynthesizeSlotsFromStructure(room, slotKey);
        }

        static bool InteriorZonesHaveSlots(GdDict interior)
        {
            foreach (string key in new[] { "wall_slots", "center_slots", "reserved_cells" })
                if (interior.Get(key, new GdArray()) is GdArray values && !values.IsEmpty) return true;
            return false;
        }

        static GdArray NormalizeSlots(GdArray raw, bool againstWall)
        {
            var output = new GdArray();
            foreach (var item in raw)
            {
                object cellValue = item;
                bool wallFlag = againstWall;
                GdDict extra = new GdDict();
                if (item is GdDict row)
                {
                    cellValue = row.Get("cell", "");
                    wallFlag = V.Bool(row.Get("against_wall", againstWall));
                    extra = row.DeepCopy();
                }
                GdArray parsed = LayoutSerializer.ParseSlotCell(cellValue);
                GdDict entry = !extra.IsEmpty ? extra : new GdDict();
                entry["against_wall"] = wallFlag;
                entry["cell"] = parsed.Count >= 2 ? parsed : cellValue;
                output.Append(entry);
            }
            return output;
        }

        static GdDict ReservedCellKeys(GdDict room, string roomId)
        {
            var keys = new GdDict();
            if (!(room.Get("interior_zones", new GdDict()) is GdDict interior)) return keys;
            if (!(interior.Get("reserved_cells", new GdArray()) is GdArray reserved)) return keys;
            foreach (var cell in reserved)
            {
                string key = CellOccupancyKey(roomId, LayoutSerializer.ParseSlotCell(cell));
                if (key.Length != 0) keys[key] = true;
            }
            return keys;
        }

        static string CellOccupancyKey(string roomId, GdArray cell)
        {
            if (cell.Count < 2) return "";
            return roomId + "|" + GdString.FormatInt(V.I64(cell[0])) + "|" + GdString.FormatInt(V.I64(cell[1]));
        }

        /// <summary>Derive wall_slots / center_slots from floor structural placements (max 3 wall, 1 center).</summary>
        static GdArray SynthesizeSlotsFromStructure(GdDict room, string slotKey)
        {
            var floors = new GdArray();
            if (!(room.Get("structural_placements", new GdArray()) is GdArray placements)) return new GdArray();
            foreach (var pVariant in placements)
            {
                if (!(pVariant is GdDict p)) continue;
                string kind = V.Str(p.Get("module_id", p.Get("module", ""))).ToLowerInvariant();
                if (!(GdString.BeginsWith(kind, "floor") || GdString.Find(kind, "floor") >= 0)) continue;
                string cell = V.Str(p.Get("name", p.Get("cell", "")));
                object posV = p.Get("world_position", null);
                floors.Append(new GdDict { { "cell", cell }, { "world_position", posV }, { "against_wall", slotKey == "wall_slots" } });
            }
            if (floors.IsEmpty) return new GdArray();
            if (slotKey == "center_slots") return GdArray.Of(floors[0]);
            if (slotKey == "wall_slots")
            {
                var output = new GdArray();
                int n = System.Math.Min(3, floors.Count);
                for (int i = 0; i < n; i++)
                {
                    GdDict row = ((GdDict)floors[i]).DeepCopy();
                    row["against_wall"] = true;
                    output.Append(row);
                }
                return output;
            }
            return new GdArray();
        }

        static string WeightedPick(GdArray choices, GodotRandom rng)
        {
            long total = 0;
            var weights = new List<(string Id, long W)>();
            foreach (var c in choices)
            {
                if (!(c is GdDict cd)) continue;
                long w = System.Math.Max(1L, V.I64(cd.Get("weight", 1L)));
                weights.Add((V.Str(cd.Get("component_id", "")), w));
                total += w;
            }
            if (total <= 0 || weights.Count == 0) return "";
            long roll = rng.RandiRange(1, total);
            long cum = 0;
            foreach (var row in weights)
            {
                cum += row.W;
                if (roll <= cum) return row.Id;
            }
            return weights[weights.Count - 1].Id;
        }

        /// <summary>
        /// Attach linked_system/subcomponent for catalog-linked pieces, then soft-link unlinked placements onto
        /// uncovered subcomponents of <paramref name="systemsDoc"/> (PKG-REQ-CMP-002). Returns the linked count.
        /// </summary>
        public long LinkShipSystems(GdDict systemsDoc, ComponentCatalog catalog = null)
        {
            long linked = 0;
            if (!(systemsDoc.Get("systems", new GdArray()) is GdArray systems)) return 0;
            // Re-stamp from catalog definitions when present (authoritative for named machines).
            if (catalog != null)
            {
                for (int i = 0; i < Placed.Count; i++)
                {
                    if (!(Placed[i] is GdDict e)) continue;
                    string cid = V.Str(e.Get("component_id", ""));
                    if (cid.Length == 0 || !catalog.HasComponent(cid)) continue;
                    GdDict def = catalog.GetComponent(cid);
                    string ls = V.Str(def.Get("linked_system", ""));
                    string lsub = V.Str(def.Get("linked_subcomponent", ""));
                    if (ls.Length != 0)
                    {
                        e["linked_system"] = ls;
                        e["linked_subcomponent"] = lsub;
                    }
                }
            }
            // Index covered system.sub keys.
            var covered = new GdDict();
            foreach (var entry in Placed)
            {
                if (!(entry is GdDict e)) continue;
                string sys = V.Str(e.Get("linked_system", ""));
                string sub = V.Str(e.Get("linked_subcomponent", ""));
                if (sys.Length != 0 && sub.Length != 0)
                {
                    covered[sys + "." + sub] = true;
                    linked += 1;
                }
            }
            // Uncovered subcomponent queue.
            var uncovered = new List<(string System, string Sub)>();
            foreach (var sysVariant in systems)
            {
                if (!(sysVariant is GdDict sysRow)) continue;
                string sid = V.Str(sysRow.Get("system_id", sysRow.Get("id", "")));
                if (sid.Length == 0) continue;
                if (!(sysRow.Get("subcomponents", new GdArray()) is GdArray subs)) continue;
                foreach (var subVariant in subs)
                {
                    if (!(subVariant is GdDict subRow)) continue;
                    string subId = V.Str(subRow.Get("subcomponent_id", ""));
                    if (subId.Length == 0) continue;
                    if (!covered.Has(sid + "." + subId)) uncovered.Add((sid, subId));
                }
            }
            // Soft-link unlinked placements onto uncovered subs (deterministic order).
            int u = 0;
            for (int i2 = 0; i2 < Placed.Count; i2++)
            {
                if (u >= uncovered.Count) break;
                if (!(Placed[i2] is GdDict e2)) continue;
                if (V.Str(e2.Get("linked_system", "")).Length != 0) continue;
                var assign = uncovered[u];
                u += 1;
                e2["linked_system"] = assign.System;
                e2["linked_subcomponent"] = assign.Sub;
                e2["soft_linked"] = true;
                covered[V.Str(e2["linked_system"]) + "." + V.Str(e2["linked_subcomponent"])] = true;
                linked += 1;
            }
            return linked;
        }

        static string SlotKey(GdDict e) =>
            V.Str(e.Get("room_id", "")) + "|" + V.Str(e.Get("slot_kind", "")) + "|" + GdString.FormatInt(V.I64(e.Get("slot_index", 0L)));

        /// <summary>Sorted, de-duplicated <c>room|slot_kind|index</c> keys (<c>PackedStringArray</c>).</summary>
        public List<string> OccupancyKeys()
        {
            var keys = new List<string>();
            var seen = new HashSet<string>();
            foreach (var entry in Placed)
            {
                if (!(entry is GdDict e)) continue;
                string k = SlotKey(e);
                if (!seen.Add(k)) continue;
                keys.Add(k);
            }
            GdString.SortStrings(keys);
            return keys;
        }

        public bool HasSlotCollisions()
        {
            var seen = new HashSet<string>();
            foreach (var entry in Placed)
            {
                if (!(entry is GdDict e)) continue;
                if (!seen.Add(SlotKey(e))) return true;
            }
            return false;
        }

        public GdDict GetSummary() => new GdDict
        {
            { "schema", "component_placement_v1" },
            { "seed", SeedValue },
            { "count", (long)Placed.Count },
            { "placed", Placed.DeepCopy() },
        };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            SeedValue = V.I64(summary.Get("seed", 0L));
            if (!(summary.Get("placed", new GdArray()) is GdArray p)) return false;
            Placed = p.DeepCopy();
            return true;
        }

        public string Fingerprint()
        {
            var parts = new List<string>();
            foreach (var entry in Placed)
            {
                if (!(entry is GdDict e)) continue;
                parts.Add(V.Str(e.Get("room_id", "")) + ":" + V.Str(e.Get("slot_kind", "")) + ":" +
                          V.Str(e.Get("component_id", "")) + ":" + GdString.FormatInt(V.I64(e.Get("slot_index", 0L))));
            }
            return string.Join("|", parts);
        }

        // --- PKG-B2.3b: mount / dismount pure ops (WorkAction resolve targets) ---

        public long FindIndex(string instanceId)
        {
            for (int i = 0; i < Placed.Count; i++)
            {
                if (!(Placed[i] is GdDict e)) continue;
                if (V.Str(e.Get("component_instance_id", "")) == instanceId) return i;
            }
            return -1;
        }

        public GdDict GetEntry(string instanceId)
        {
            long idx = FindIndex(instanceId);
            if (idx < 0) return new GdDict();
            return ((GdDict)Placed[(int)idx]).DeepCopy();
        }

        public bool IsMounted(string instanceId)
        {
            GdDict e = GetEntry(instanceId);
            if (e.IsEmpty) return false;
            return V.Bool(e.Get("mounted", true));
        }

        /// <summary>Dismounts a placed component (mounted=false) and returns the yield payload; inventory is untouched.</summary>
        public GdDict Dismount(string instanceId)
        {
            var output = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "item_form", "" },
                { "mass", 0.0 },
                { "qty", 0L },
                { "component_id", "" },
                { "instance_id", instanceId },
            };
            long idx = FindIndex(instanceId);
            if (idx < 0)
            {
                output["reason"] = "not_found";
                return output;
            }
            var e = (GdDict)Placed[(int)idx];
            if (!V.Bool(e.Get("mounted", true)))
            {
                output["reason"] = "already_dismounted";
                return output;
            }
            string itemForm = V.Str(e.Get("item_form", e.Get("component_id", "")));
            if (itemForm.Length == 0)
            {
                output["reason"] = "no_item_form";
                return output;
            }
            e["mounted"] = false;
            output["ok"] = true;
            output["item_form"] = itemForm;
            output["mass"] = V.F64(e.Get("mass", 10.0));
            output["qty"] = 1L;
            output["component_id"] = V.Str(e.Get("component_id", ""));
            output["linked_system"] = V.Str(e.Get("linked_system", ""));
            output["linked_subcomponent"] = V.Str(e.Get("linked_subcomponent", ""));
            return output;
        }

        /// <summary>
        /// Remounts into a free or previously emptied slot, consuming one <paramref name="itemForm"/> from
        /// <paramref name="inventory"/> (item_id -&gt; qty) on success.
        /// </summary>
        public GdDict Mount(string itemForm, string roomId, string slotKind, long slotIndex, GdDict inventory, ComponentCatalog catalog = null)
        {
            var output = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "instance_id", "" },
                { "item_form", itemForm },
            };
            if (string.IsNullOrEmpty(itemForm))
            {
                output["reason"] = "no_item";
                return output;
            }
            if (V.I64(inventory.Get(itemForm, 0L)) < 1)
            {
                output["reason"] = "missing_item";
                return output;
            }
            // Prefer remounting an existing dismounted entry in this slot.
            int targetIdx = -1;
            for (int i = 0; i < Placed.Count; i++)
            {
                if (!(Placed[i] is GdDict e)) continue;
                if (V.Str(e.Get("room_id", "")) != roomId) continue;
                if (V.Str(e.Get("slot_kind", "")) != slotKind) continue;
                if (V.I64(e.Get("slot_index", -1L)) != slotIndex) continue;
                targetIdx = i;
                break;
            }
            if (targetIdx >= 0)
            {
                var existing = (GdDict)Placed[targetIdx];
                if (V.Bool(existing.Get("mounted", true)))
                {
                    output["reason"] = "slot_occupied";
                    return output;
                }
                // Must match the item form that was removed.
                string want = V.Str(existing.Get("item_form", existing.Get("component_id", "")));
                if (want != itemForm)
                {
                    output["reason"] = "wrong_item";
                    return output;
                }
                existing["mounted"] = true;
                inventory[itemForm] = V.I64(inventory.Get(itemForm, 0L)) - 1;
                if (V.I64(inventory[itemForm]) <= 0) inventory.Erase(itemForm);
                output["ok"] = true;
                output["instance_id"] = V.Str(existing.Get("component_instance_id", ""));
                return output;
            }
            // Fresh mount into an empty slot — the catalog resolves component_id from item_form.
            if (catalog == null)
            {
                output["reason"] = "slot_empty_needs_catalog";
                return output;
            }
            string componentId = catalog.ComponentIdForItemForm(itemForm);
            if (componentId.Length == 0)
            {
                output["reason"] = "unknown_item_form";
                return output;
            }
            GdDict def = catalog.GetComponent(componentId);
            var entry = new GdDict
            {
                { "component_instance_id", roomId + "_" + slotKind + "_" + GdString.FormatInt(slotIndex) },
                { "component_id", componentId },
                { "room_id", roomId },
                { "slot_kind", slotKind },
                { "slot_index", slotIndex },
                { "cell", "" },
                { "against_wall", slotKind == "wall" },
                { "condition", V.F64(def.Get("condition_default", 1.0)) },
                { "item_form", itemForm },
                { "mass", V.F64(def.Get("mass", 10.0)) },
                { "linked_system", V.Str(def.Get("linked_system", "")) },
                { "linked_subcomponent", V.Str(def.Get("linked_subcomponent", "")) },
                { "mounted", true },
            };
            // Collision check.
            if (OccupancyKeys().Contains(roomId + "|" + slotKind + "|" + GdString.FormatInt(slotIndex)))
            {
                output["reason"] = "slot_occupied";
                return output;
            }
            Placed.Append(entry);
            inventory[itemForm] = V.I64(inventory.Get(itemForm, 0L)) - 1;
            if (V.I64(inventory[itemForm]) <= 0) inventory.Erase(itemForm);
            output["ok"] = true;
            output["instance_id"] = V.Str(entry["component_instance_id"]);
            return output;
        }

        public long MountedCount()
        {
            long n = 0;
            foreach (var entry in Placed)
            {
                if (!(entry is GdDict e)) continue;
                if (V.Bool(e.Get("mounted", true))) n += 1;
            }
            return n;
        }

        public long DismountedCount() => Placed.Count - MountedCount();
    }
}
