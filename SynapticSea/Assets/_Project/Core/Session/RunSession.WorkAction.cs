// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: component markers (3700-3766), the WorkAction channel
// (3770-3848, 4149-4880), ship modification (3988-4105), module-integrity scene consequences (4917-4963, 7466-7479).
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
        // ------------------------------------------------------------------ component markers
        /// <summary>PKG-B2.3: mounted-component marker records (world positions) for the Runtime to render.</summary>
        void RebuildComponentMarkers()
        {
            ClearComponentMarkers();
            if (ComponentPlacementState == null)
                return;
            GdDict layout = ActiveLayoutForWork();
            GdDict centers = RoomWorldCenters(layout);
            long i = 0;
            foreach (object entryObj in ComponentPlacementState.Placed)
            {
                if (!(entryObj is GdDict e))
                    continue;
                if (!V.Bool(e.Get("mounted", true)))
                    continue;
                Vec3 pos = ComponentMarkerWorld(layout, e, centers, i);
                ComponentMarkers.Add(new GdDict
                {
                    { "component_instance_id", V.Str(e.Get("component_instance_id", i)) },
                    { "component_id", V.Str(e.Get("component_id", "")) },
                    { "room_id", V.Str(e.Get("room_id", "")) },
                    { "world_position", pos },
                });
                i += 1;
            }
            Events.RaiseComponentMarkersRebuilt(ComponentMarkers);
        }

        void ClearComponentMarkers()
        {
            ComponentMarkers.Clear();
            Events.RaiseComponentMarkersRebuilt(ComponentMarkers);
        }

        // ------------------------------------------------------------------ work action
        /// <summary>
        /// Start + instantly complete a catalog WorkAction against the live module map (was
        /// <c>run_work_action_for_validation</c>). Returns the resolve dict.
        /// </summary>
        public GdDict RunWorkAction(string actionId, string targetId, GdDict inventoryOverride = null)
        {
            if (WorkActionDriver == null)
            {
                WorkActionDriver = new WorkActionDriver();
                WorkActionDriver.Configure(new GdDict());
            }
            GdDict inv = inventoryOverride ?? new GdDict();
            if (inv.IsEmpty && InventoryState != null)
                inv = InventoryState.Items.DeepCopy();
            var ctx = new GdDict
            {
                { "tool_class", "" },
                { "skill_id", "salvage" },
                { "skill_level", 0L },
                { "inventory", inv.DeepCopy() },
            };
            if (WorkActionDriver.Catalog != null && WorkActionDriver.Catalog.HasAction(actionId))
            {
                GdDict def = WorkActionDriver.Catalog.GetAction(actionId);
                ctx["tool_class"] = V.Str(def.Get("tool_class", ""));
                ctx["skill_id"] = V.Str(def.Get("min_skill", "salvage"));
                if (V.Str(ctx["skill_id"]).Length == 0)
                    ctx["skill_id"] = "salvage";
                if (PlayerProgression != null)
                    ctx["skill_level"] = PlayerProgression.GetSkillLevel(V.Str(ctx["skill_id"]));
                if (def.Get("materials_consumed", null) is GdDict mats)
                {
                    foreach (object mid in mats.Keys)
                    {
                        if (V.I64(inv.Get(V.Str(mid), 0L)) < V.I64(mats[mid]))
                            inv[V.Str(mid)] = V.I64(mats[mid]);
                    }
                }
                ctx["inventory"] = inv.DeepCopy();
            }
            if (!WorkActionDriver.StartAction(actionId, targetId, ctx))
                return new GdDict { { "ok", false }, { "reason", "start_failed" } };
            _workRequiresHold = false;
            WorkActionDriver.Tick(999.0, new GdDict { { "work_speed_mult", V.F64(ctx.Get("work_speed_mult", 1.0)) } });
            GdDict res = WorkActionDriver.Complete(ModuleIntegrityMap, inv);
            RefreshWorkActionHud();
            if (res.GetBool("ok"))
            {
                if (WorkActionDriver.LastNoisePulse > 0.0 && ThreatManager != null)
                    WorkActionDriver.ApplyNoiseToDetection(ThreatManager);
                if (res.Has("audio_event"))
                    PlaySfx(V.Str(res.Get("audio_event", "")));
                string xpEv = WorkActionDriver.LastXpEvent;
                if (xpEv.Length == 0)
                    xpEv = V.Str(res.Get("xp_event", ""));
                if (xpEv.Length > 0)
                    EmitTrainingEvent(xpEv, targetId);
                ApplyModuleIntegrityStateToScene();
                ApplyWorkYieldsToInventoryState(res);
            }
            return res;
        }

        /// <summary><c>_refresh_work_action_hud()</c> -> <see cref="SessionEvents.WorkActionHudState"/>.</summary>
        internal void RefreshWorkActionHud()
        {
            if (WorkActionDriver == null)
                return;
            string st = WorkActionDriver.GetStatus();
            string actionId = "";
            string targetId = "";
            string verb = "";
            double noise = WorkActionDriver.LastNoisePulse;
            if (WorkActionDriver.Work != null)
            {
                actionId = WorkActionDriver.Work.ActionId;
                targetId = WorkActionDriver.Work.TargetId;
                GdDict sum = WorkActionDriver.Work.GetSummary();
                GdDict def = sum.Get("definition", null) as GdDict ?? new GdDict();
                verb = V.Str(def.Get("verb", ""));
            }
            Events.RaiseWorkActionHudState(new GdDict
            {
                { "action_id", actionId },
                { "target_id", targetId },
                { "verb", verb },
                { "progress", WorkActionDriver.ProgressRatio() },
                { "status", st },
                { "noise", noise },
            });
        }

        // ------------------------------------------------------------------ B3: hold vs tap (Unity-port input API)
        bool _workHoldInput;

        /// <summary>
        /// True when work actions need interact held (the default); false when <see cref="SettingsState"/>
        /// <c>hold_to_tap</c> is on (tap starts the action, it runs on its own, tap again cancels). Read live, so a settings
        /// change applies to the next frame.
        /// </summary>
        public bool HoldToWorkEnabled => SettingsState == null || !SettingsState.IsHoldToTap();

        /// <summary>True while the input layer holds interact (<see cref="BeginWorkHold"/>) or the frame reports it held.</summary>
        public bool IsWorkInteractHeld => _workHoldInput || (_inTick && _frame.InteractHeld);

        /// <summary>
        /// The input layer calls this on EVERY interact press. Returns true when the press was consumed by an in-progress
        /// work action (do not also call <see cref="RequestInteract"/>):
        /// <list type="bullet">
        /// <item>hold mode: an active action resumes while interact stays held (progress pauses on release, Godot);</item>
        /// <item>tap mode: an active action is cancelled.</item>
        /// </list>
        /// Returns false when nothing is in progress: dispatch <see cref="RequestInteract"/> as usual (a work action it starts
        /// progresses while the hold continues, or on its own in tap mode).
        /// </summary>
        public bool BeginWorkHold()
        {
            _workHoldInput = true;
            if (WorkActionDriver == null || !WorkActionDriver.IsWorking())
                return false;
            if (!HoldToWorkEnabled)
                CancelWorkAction();
            return true;
        }

        /// <summary>The input layer calls this on interact release: in hold mode progress pauses until the next hold.</summary>
        public void EndWorkHold()
        {
            _workHoldInput = false;
        }

        /// <summary>Cancels the in-progress work action (interrupt; progress is lost). False when nothing was in progress.</summary>
        public bool CancelWorkAction()
        {
            if (WorkActionDriver == null || !WorkActionDriver.IsWorking())
                return false;
            WorkActionDriver.Work?.Interrupt();
            _workRequiresHold = false;
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            RefreshWorkActionHud();
            return true;
        }

        /// <summary>Lowest-priority interact: dismount/remount the nearest component (wrench) or weld/cut/pry structure.</summary>
        internal bool TryWorkActionInteract(Vec3 playerPos)
        {
            if (!HasPlayer || WorkActionDriver == null)
                return false;
            if (WorkActionDriver.IsWorking())
            {
                WorkActionDriver.Work?.Interrupt();
                _workRequiresHold = false;
                RefreshWorkActionHud();
                return true;
            }
            GdDict layout = ActiveLayoutForWork();
            if (layout.IsEmpty)
                return false;
            GdDict inv = InventoryQtyDictForWork();
            string actionId = "";
            string toolClass = "";
            string targetId = "";
            bool hasWrench = V.I64(inv.Get("wrench", 0L)) > 0 || V.I64(inv.Get("tool_wrench", 0L)) > 0;
            if (hasWrench && ComponentPlacementState != null)
            {
                GdDict remount = NearestRemountTarget(layout, playerPos, inv, WORK_ACTION_INTERACT_RANGE);
                if (!remount.IsEmpty)
                {
                    actionId = "mount_component";
                    toolClass = "wrench";
                    targetId = V.Str(remount.Get("target_id", ""));
                }
                else
                {
                    GdDict comp = NearestMountedComponent(layout, playerPos, WORK_ACTION_INTERACT_RANGE);
                    if (!comp.IsEmpty)
                    {
                        actionId = "dismount_component";
                        toolClass = "wrench";
                        targetId = V.Str(comp.Get("component_instance_id", ""));
                    }
                }
            }
            if (actionId.Length == 0)
            {
                if (ModuleIntegrityMap == null)
                    ModuleIntegrityMap = new ModuleIntegrityMap();
                if (ModuleIntegrityMap.Size() == 0)
                    ModuleIntegrityConsequences.SeedMapFromCompiledLayout(ModuleIntegrityMap, layout);
                bool hasLance = V.I64(inv.Get("welding_lance", 0L)) > 0 || V.I64(inv.Get("tool_welding_lance", 0L)) > 0;
                bool hasPlate = V.I64(inv.Get("hull_plate", 0L)) > 0 || V.I64(inv.Get("plating_plate", 0L)) > 0 || V.I64(inv.Get("hull_plate_kit", 0L)) > 0;
                if (hasLance && hasPlate)
                {
                    GdDict damaged = NearestDamagedWallModule(layout, playerPos, WORK_ACTION_INTERACT_RANGE);
                    if (!damaged.IsEmpty)
                    {
                        targetId = V.Str(damaged.Get("module_id", ""));
                        string dkind = V.Str(damaged.Get("kind", "wall_straight_1x1"));
                        if (targetId.Length > 0)
                        {
                            ModuleIntegrityMap.EnsureModule(targetId, dkind, new GdDict(), SliceBeforeSlash(targetId));
                            actionId = "weld_patch";
                            toolClass = "welding_lance";
                            if (V.I64(inv.Get("hull_plate", 0L)) < 1)
                            {
                                if (V.I64(inv.Get("plating_plate", 0L)) > 0)
                                    inv["hull_plate"] = V.I64(inv.Get("plating_plate", 0L));
                                else if (V.I64(inv.Get("hull_plate_kit", 0L)) > 0)
                                    inv["hull_plate"] = V.I64(inv.Get("hull_plate_kit", 0L));
                            }
                        }
                    }
                }
                if (actionId.Length == 0)
                {
                    GdDict nearest = NearestWorkableWallModule(layout, playerPos, WORK_ACTION_INTERACT_RANGE);
                    if (nearest.IsEmpty)
                        return false;
                    targetId = V.Str(nearest.Get("module_id", ""));
                    if (targetId.Length == 0)
                        return false;
                    string kind = V.Str(nearest.Get("kind", "wall_straight_1x1"));
                    ModuleIntegrityMap.EnsureModule(targetId, kind, new GdDict(), SliceBeforeSlash(targetId));
                    if (hasLance)
                    {
                        actionId = "cut_wall";
                        toolClass = "welding_lance";
                    }
                    else if (V.I64(inv.Get("prybar", 0L)) > 0 || V.I64(inv.Get("tool_prybar", 0L)) > 0)
                    {
                        actionId = "pry_panel";
                        toolClass = "prybar";
                    }
                    else
                    {
                        PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                        return true;
                    }
                }
            }
            if (actionId.Length == 0 || targetId.Length == 0)
                return false;
            if (VitalsState != null && VitalsState.Stamina <= 0.001)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            string skillId = "salvage";
            if (actionId == "weld_patch" || actionId == "patch_breach" || actionId == "splice_conduit")
                skillId = "repair";
            long skillLevel = PlayerProgression != null ? PlayerProgression.GetSkillLevel(skillId) : 0;
            var ctx = new GdDict
            {
                { "tool_class", toolClass },
                { "skill_id", skillId },
                { "skill_level", skillLevel },
                { "inventory", inv },
            };
            if (!WorkActionDriver.StartAction(actionId, targetId, ctx))
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            // Unity port (B3): hold-to-work unless the player chose hold_to_tap (then the action runs on its own).
            _workRequiresHold = HoldToWorkEnabled;
            RefreshWorkActionHud();
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            return true;
        }

        /// <summary>Godot <c>String.get_slice("/", 0)</c>.</summary>
        static string SliceBeforeSlash(string s)
        {
            int cut = s.IndexOf('/');
            return cut < 0 ? s : s.Substring(0, cut);
        }

        GdDict NearestMountedComponent(GdDict layout, Vec3 playerPos, double maxRange)
        {
            var best = new GdDict();
            double bestD = maxRange;
            if (ComponentPlacementState == null)
                return best;
            GdDict roomCenters = RoomWorldCenters(layout);
            long i = 0;
            foreach (object entryObj in ComponentPlacementState.Placed)
            {
                if (!(entryObj is GdDict e) || !V.Bool(e.Get("mounted", true)))
                {
                    i += 1;
                    continue;
                }
                Vec3 pos = ComponentMarkerWorld(layout, e, roomCenters, i);
                double d = playerPos.DistanceTo(pos);
                if (d <= bestD)
                {
                    bestD = d;
                    best = e.DeepCopy();
                    best["distance"] = d;
                }
                i += 1;
            }
            return best;
        }

        GdDict NearestRemountTarget(GdDict layout, Vec3 playerPos, GdDict inventory, double maxRange)
        {
            var best = new GdDict();
            double bestD = maxRange;
            if (ComponentPlacementState == null)
                return best;
            GdDict roomCenters = RoomWorldCenters(layout);
            long i = 0;
            foreach (object entryObj in ComponentPlacementState.Placed)
            {
                if (!(entryObj is GdDict e) || V.Bool(e.Get("mounted", true)))
                {
                    i += 1;
                    continue;
                }
                string form = V.Str(e.Get("item_form", e.Get("component_id", "")));
                if (form.Length == 0 || V.I64(inventory.Get(form, 0L)) < 1)
                {
                    i += 1;
                    continue;
                }
                string rid = V.Str(e.Get("room_id", ""));
                Vec3 pos = ComponentMarkerWorld(layout, e, roomCenters, i);
                double d = playerPos.DistanceTo(pos);
                if (d > bestD)
                {
                    i += 1;
                    continue;
                }
                bestD = d;
                best = new GdDict
                {
                    { "target_id", rid + "|" + V.Str(e.Get("slot_kind", "wall")) + "|" + V.I64(e.Get("slot_index", 0L)) + "|" + form },
                    { "distance", d },
                    { "item_form", form },
                };
                i += 1;
            }
            return best;
        }

        /// <summary><c>_slot_occupancy_from_loader()</c>: loot/objective/start-room/dressing cells already taken.</summary>
        GdDict SlotOccupancyFromLoader()
        {
            var occupied = new GdDict();
            IShipLoaderView activeLoader = AwayFromStart && CurrentShip != null ? CurrentShip.SceneRoot as IShipLoaderView : Loader;
            if (activeLoader == null || !activeLoader.IsValid)
                return occupied;
            foreach (object lootRow in activeLoader.GetLootContainerSpecsCopy())
            {
                if (lootRow is GdDict loot)
                    MarkOccupancy(occupied, V.Str(loot.Get("room_id", "")), loot.Get("approach_cell", new GdArray()));
            }
            GdDict gameplay = activeLoader.GameplayDoc ?? new GdDict();
            foreach (object objRow in gameplay.GetArrayOrEmpty("objectives"))
            {
                if (objRow is GdDict obj)
                    MarkObjectiveOccupancy(occupied, obj);
            }
            foreach (object specRow in activeLoader.GetObjectiveSpecsCopy())
            {
                if (specRow is GdDict spec)
                    MarkObjectiveOccupancy(occupied, spec);
            }
            string startRoom = V.Str(gameplay.Get("start_room", ""));
            GdDict layoutDoc = activeLoader.LayoutDoc;
            if (startRoom.Length == 0 && layoutDoc != null && layoutDoc.Get("prototype", null) is GdDict proto)
                startRoom = V.Str(proto.Get("start_room", ""));
            if (startRoom.Length > 0 && layoutDoc != null)
            {
                foreach (object roomV in layoutDoc.GetArrayOrEmpty("rooms"))
                {
                    if (!(roomV is GdDict room))
                        continue;
                    if (V.Str(room.Get("id", "")) != startRoom)
                        continue;
                    GdArray boardingCell = GameplaySliceBuilder.BoardingCellXz(room);
                    if (boardingCell.Count >= 2)
                        MarkOccupancy(occupied, startRoom, boardingCell);
                    break;
                }
            }
            foreach (object rowObj in activeLoader.DressingPropSlots() ?? new GdArray())
            {
                if (!(rowObj is GdDict row))
                    continue;
                string name = V.Str(row.Get("name", ""));
                if (!GdString.BeginsWith(name, "DressingProp_"))
                    continue;
                GdArray parsed = LayoutSerializer.ParseSlotCell(row.Get("slot_cell", new GdArray()));
                string roomId = GdString.TrimPrefix(name, "DressingProp_");
                int cut = GdString.RFind(roomId, "_");
                if (cut >= 0)
                    roomId = roomId.Substring(0, cut);
                if (parsed.Count >= 2 && roomId.Length > 0)
                    occupied[roomId + "|" + V.I64(parsed[0]) + "|" + V.I64(parsed[1])] = true;
            }
            return occupied;
        }

        static void MarkObjectiveOccupancy(GdDict occupied, GdDict obj)
        {
            string roomId = V.Str(obj.Get("room_id", ""));
            MarkOccupancy(occupied, roomId, obj.Get("approach_cell", new GdArray()));
            if (!(obj.Get("steps", null) is GdArray steps))
                return;
            foreach (object stepV in steps)
            {
                if (!(stepV is GdDict step))
                    continue;
                string stepRoom = V.Str(step.Get("room_id", roomId));
                MarkOccupancy(occupied, stepRoom, step.Get("approach_cell", new GdArray()));
            }
        }

        static void MarkOccupancy(GdDict occupied, string roomId, object cellV)
        {
            if (string.IsNullOrEmpty(roomId))
                return;
            GdArray parsed = LayoutSerializer.ParseSlotCell(cellV);
            if (parsed.Count >= 2)
                occupied[roomId + "|" + V.I64(parsed[0]) + "|" + V.I64(parsed[1])] = true;
        }

        Vec3 ComponentMarkerWorld(GdDict layout, GdDict entry, GdDict centers, long i)
        {
            string rid = V.Str(entry.Get("room_id", ""));
            GdArray parsed = LayoutSerializer.ParseSlotCell(entry.Get("cell", null));
            if (parsed.Count >= 2)
            {
                var room = new GdDict();
                foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
                {
                    if (roomV is GdDict r && V.Str(r.Get("id", "")) == rid)
                    {
                        room = r;
                        break;
                    }
                }
                long deck = V.I64(room.Get("deck", 0L));
                Vec3 world = SlotCellToWorld(layout, room, parsed, deck);
                if (world != Vec3.Inf)
                {
                    if (CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                        return ToGlobal(CurrentShip.SceneRoot, world);
                    return world;
                }
            }
            var fallback = new Vec3(i * 0.5, 0.5, 0.0);
            return centers.Has(rid) && centers[rid] is Vec3 c ? c : fallback;
        }

        static Vec3 SlotCellToWorld(GdDict layout, GdDict room, GdArray cell, long deck)
        {
            if (cell.Count < 2)
                return Vec3.Inf;
            string roomId = V.Str(room.Get("id", ""));
            string cellKeyValue = deck + "|" + V.I64(cell[0]) + "|" + V.I64(cell[1]);
            if (layout.Get("structural_plan", null) is GdDict plan && plan.Get("floor_placements", null) is GdArray floors)
            {
                foreach (object floorV in floors)
                {
                    if (!(floorV is GdDict floor))
                        continue;
                    if (V.Str(floor.Get("cell_key", "")) != cellKeyValue)
                        continue;
                    if (roomId.Length > 0 && V.Str(floor.Get("room_id", "")) != roomId)
                        continue;
                    object posV = floor.Get("world_position", floor.Get("position", null));
                    if (posV is GdArray a && a.Count >= 3)
                        return new Vec3(V.F64(a[0]), V.F64(a[1]) + 0.12, V.F64(a[2]));
                    if (posV is Vec3 v)
                        return new Vec3(v.X, (float)(v.Y + 0.12), v.Z);
                }
            }
            string name1 = "floor_cell_x" + V.I64(cell[0]) + "_z" + V.I64(cell[1]);
            string name2 = "floor_cell_d" + deck + "_x" + V.I64(cell[0]) + "_z" + V.I64(cell[1]);
            foreach (object pV in room.GetArrayOrEmpty("structural_placements"))
            {
                if (!(pV is GdDict p))
                    continue;
                string pname = V.Str(p.Get("name", ""));
                if (pname != name1 && pname != name2)
                    continue;
                if (p.Get("world_position", null) is GdArray a2 && a2.Count >= 3)
                    return new Vec3(V.F64(a2[0]), V.F64(a2[1]) + 0.12, V.F64(a2[2]));
            }
            return new Vec3(V.I64(cell[0]) * 4.0, deck * 4.0 + 0.12, V.I64(cell[1]) * 4.0);
        }

        GdDict RoomWorldCenters(GdDict layout)
        {
            var output = new GdDict();
            if (!(layout.Get("rooms", null) is GdArray rooms))
                return output;
            foreach (object roomV in rooms)
            {
                if (!(roomV is GdDict room))
                    continue;
                string rid = V.Str(room.Get("id", ""));
                if (rid.Length == 0)
                    continue;
                Vec3 acc = Vec3.Zero;
                long n = 0;
                foreach (object pV in room.GetArrayOrEmpty("structural_placements"))
                {
                    if (pV is GdDict p && p.Get("world_position", null) is GdArray a && a.Count >= 3)
                    {
                        acc += new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2]));
                        n += 1;
                    }
                }
                if (n <= 0)
                    continue;
                Vec3 local = acc / (float)n;
                if (CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                    local = ToGlobal(CurrentShip.SceneRoot, local);
                output[rid] = local;
            }
            return output;
        }

        GdDict ActiveLayoutForWork()
        {
            if (CurrentShip != null && CurrentShip.BuiltLayout != null && !CurrentShip.BuiltLayout.IsEmpty)
                return CurrentShip.BuiltLayout;
            if (Loader != null)
                return Loader.GetLayoutCopy();
            return new GdDict();
        }

        GdDict InventoryQtyDictForWork() => InventoryState == null ? new GdDict() : InventoryState.Items.DeepCopy();

        GdDict NearestDamagedWallModule(GdDict layout, Vec3 playerPos, double maxRange)
        {
            GdDict cand = NearestWorkableWallModule(layout, playerPos, maxRange);
            if (cand.IsEmpty || ModuleIntegrityMap == null)
                return new GdDict();
            string mid = V.Str(cand.Get("module_id", ""));
            if (mid.Length == 0)
                return new GdDict();
            string st = ModuleIntegrityMap.GetState(mid);
            if (st == "damaged" || st == "breached")
                return cand;
            var best = new GdDict();
            double bestD = maxRange;
            GdDict roomCenters = RoomWorldCenters(layout);
            foreach (string id in ModuleIntegrityMap.ModuleIds())
            {
                string st2 = ModuleIntegrityMap.GetState(id);
                if (st2 != "damaged" && st2 != "breached")
                    continue;
                ModuleIntegrityState m = ModuleIntegrityMap.GetModule(id);
                Vec3 pos = CompiledWrapperWorldPosition(id);
                if (pos == Vec3.Inf)
                {
                    string rid = m != null ? m.RoomId : "";
                    if (rid.Length == 0)
                    {
                        string prefix = SliceBeforeSlash(id);
                        if (prefix == "floor" || prefix == "edge" || prefix == "ceiling")
                            continue;
                        rid = prefix;
                    }
                    if (!roomCenters.Has(rid))
                        continue;
                    pos = (Vec3)roomCenters[rid];
                }
                double d = playerPos.DistanceTo(pos);
                if (d <= bestD)
                {
                    bestD = d;
                    string kind = m != null ? m.Kind : "wall";
                    best = new GdDict { { "module_id", id }, { "kind", kind }, { "distance", d } };
                }
            }
            return best;
        }

        /// <summary>{module_id, kind, distance} for the nearest workable structural module (walls preferred over floors).</summary>
        GdDict NearestWorkableWallModule(GdDict layout, Vec3 playerPos, double maxRange)
        {
            var best = new GdDict();
            double bestD = maxRange;
            long bestRank = 99;
            if (layout.Get("rooms", null) is GdArray rooms)
            {
                foreach (object roomV in rooms)
                {
                    if (!(roomV is GdDict room))
                        continue;
                    string roomId = V.Str(room.Get("id", ""));
                    if (!(room.Get("structural_placements", null) is GdArray placements))
                        continue;
                    foreach (object placementV in placements)
                    {
                        if (!(placementV is GdDict placement))
                            continue;
                        string kind = V.Str(placement.Get("module_id", placement.Get("module", "")));
                        if (kind.Length == 0)
                            continue;
                        string pname = V.Str(placement.Get("name", kind));
                        string mid = roomId + "/" + pname;
                        long rank = WorkTargetRank(kind);
                        if (rank >= 2 && bestRank < 2)
                            continue;
                        if (ModuleIntegrityMap != null && ModuleIntegrityMap.GetState(mid) == "destroyed")
                            continue;
                        object posV = placement.Get("world_position", placement.Get("position", null));
                        Vec3 pos;
                        if (posV is Vec3 pv)
                            pos = pv;
                        else if (posV is GdArray arr && arr.Count >= 3)
                            pos = new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
                        else
                            continue;
                        if (CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                            pos = ToGlobal(CurrentShip.SceneRoot, pos);
                        double d = playerPos.DistanceTo(pos);
                        if (d > maxRange)
                            continue;
                        if (rank < bestRank || (rank == bestRank && d <= bestD))
                        {
                            bestRank = rank;
                            bestD = d;
                            best = new GdDict { { "module_id", mid }, { "kind", kind }, { "distance", d } };
                        }
                    }
                }
            }
            IShipLoaderView root = null;
            if (CurrentShip != null && CurrentShip.SceneRoot is IShipLoaderView cr && cr.IsValid)
                root = cr;
            else if (Loader != null)
                root = Loader;
            if (root != null)
            {
                foreach (IStructuralModuleNode node in root.StructuralModuleNodes())
                {
                    if (!node.HasModuleKeyMeta)
                        continue;
                    string mid = node.ModuleKey;
                    string kind = node.ModuleKind ?? "";
                    if (mid.Length == 0)
                        continue;
                    long rank = WorkTargetRank(kind);
                    if (rank >= 2 && bestRank < 2)
                        continue;
                    if (ModuleIntegrityMap != null && ModuleIntegrityMap.GetState(mid) == "destroyed")
                        continue;
                    double d = playerPos.DistanceTo(node.GlobalPosition);
                    if (d <= maxRange && (rank < bestRank || (rank == bestRank && d <= bestD)))
                    {
                        bestRank = rank;
                        bestD = d;
                        best = new GdDict { { "module_id", mid }, { "kind", kind }, { "distance", d } };
                    }
                }
            }
            return best;
        }

        static long WorkTargetRank(string kind)
        {
            if (ModuleIntegrityConsequences.IsWallKind(kind))
                return 0;
            string k = kind.ToLowerInvariant();
            if (GdString.BeginsWith(k, "floor") || GdString.Find(k, "floor") >= 0 || GdString.BeginsWith(k, "ramp"))
                return 2;
            return 1;
        }

        /// <summary>PKG-B2.2b: advance in-progress work (both branches; hold-to-work freezes progress on release).</summary>
        void TickWorkAction(double delta)
        {
            if (WorkActionDriver == null || delta <= 0.0)
                return;
            if (!WorkActionDriver.IsWorking())
                return;
            // Unity port (B3): switching to hold_to_tap mid-action releases the hold requirement.
            if (_workRequiresHold && !HoldToWorkEnabled)
                _workRequiresHold = false;
            if (_workRequiresHold && !IsWorkInteractHeld)
            {
                RefreshWorkActionHud();
                return;
            }
            double speed = 1.0;
            if (WoundState != null)
                speed = WoundState.WorkSpeedMultiplier();
            if (VitalsState != null)
            {
                if (VitalsState.Stamina <= 0.001)
                {
                    InterruptWorkOnDamage();
                    _workRequiresHold = false;
                    return;
                }
                double maxS = Math.Max(1.0, VitalsState.MaxStamina);
                double sRatio = GdMath.Clampf(VitalsState.Stamina / maxS, 0.0, 1.0);
                speed *= GdMath.Clampf(0.35 + sRatio * 0.65, 0.35, 1.0);
                VitalsState.ApplyDelta(new GdDict { { "stamina", -8.0 * delta } });
            }
            WorkActionDriver.Tick(delta, new GdDict { { "work_speed_mult", speed } });
            if (WorkActionDriver.LastProgressNoise > 0.0 && ThreatManager != null)
                WorkActionDriver.ApplyNoiseToDetection(ThreatManager);
            if (WorkActionDriver.LastProgressNoise > 0.0)
                PlaySfx(AudioEventSeam.UI_WORK_PROGRESS);
            if (WorkActionDriver.GetStatus() == "completed" || (WorkActionDriver.Work != null && WorkActionDriver.Work.Status == "completed"))
            {
                GdDict inv = InventoryQtyDictForWork();
                GdDict res;
                string actionId = WorkActionDriver.Work != null ? WorkActionDriver.Work.ActionId : "";
                if (actionId == "dismount_component" || actionId == "unbolt_component")
                {
                    res = ComponentMountResolver.ResolveDismount(WorkActionDriver.Work, ComponentPlacementState, inv);
                    if (res.GetBool("ok"))
                    {
                        res["audio_event"] = AudioEventSeam.SfxForWorkVerb(V.Str(res.Get("verb", "unbolt")));
                        WorkActionDriver.LastResolve = res.DeepCopy();
                        WorkActionDriver.LastNoisePulse = V.F64(res.Get("noise", 0.0));
                        WorkActionDriver.LastXpEvent = V.Str(res.Get("xp_event", "salvage"));
                        if (InventoryState != null)
                        {
                            string form = V.Str(res.Get("item_form", ""));
                            if (form.Length > 0)
                                InventoryState.AddItem(form, 1);
                        }
                        string lsys = V.Str(res.Get("linked_system", ""));
                        string lsub = V.Str(res.Get("linked_subcomponent", ""));
                        if (lsys.Length > 0 && lsub.Length > 0)
                            ActiveSystemsManager()?.DamageSubcomponent(lsys, lsub, 1.0);
                        RebuildComponentMarkers();
                        RefreshStationTiersFromShipMod();
                    }
                }
                else if (actionId == "mount_component")
                {
                    res = ComponentMountResolver.ResolveMount(WorkActionDriver.Work, ComponentPlacementState, inv, ComponentCatalog, new GdDict());
                    if (res.GetBool("ok"))
                    {
                        res["audio_event"] = AudioEventSeam.SfxForWorkVerb(V.Str(res.Get("verb", "mount")));
                        WorkActionDriver.LastResolve = res.DeepCopy();
                        WorkActionDriver.LastNoisePulse = V.F64(res.Get("noise", 0.0));
                        WorkActionDriver.LastXpEvent = V.Str(res.Get("xp_event", "repair"));
                        if (InventoryState != null)
                        {
                            string form2 = V.Str(res.Get("item_form", ""));
                            if (form2.Length > 0)
                                InventoryState.RemoveItem(form2, 1);
                        }
                        string iid = V.Str(res.Get("instance_id", ""));
                        if (ComponentPlacementState != null && iid.Length > 0)
                        {
                            GdDict entry = ComponentPlacementState.GetEntry(iid);
                            string rsys = V.Str(entry.Get("linked_system", ""));
                            string rsub = V.Str(entry.Get("linked_subcomponent", ""));
                            if (rsys.Length > 0 && rsub.Length > 0)
                                ActiveSystemsManager()?.RestoreSubcomponentOnRemount(rsys, rsub, 0.55);
                        }
                        RebuildComponentMarkers();
                        RefreshStationTiersFromShipMod();
                    }
                }
                else
                {
                    res = WorkActionDriver.Complete(ModuleIntegrityMap, inv);
                    if (res.GetBool("ok"))
                    {
                        ApplyModuleIntegrityStateToScene();
                        ApplyWorkYieldsToInventoryState(res);
                    }
                }
                if (res.GetBool("ok"))
                {
                    if (WorkActionDriver.LastNoisePulse > 0.0 && ThreatManager != null)
                        WorkActionDriver.ApplyNoiseToDetection(ThreatManager);
                    // PKG-D10: verb completion SFX routed through the router.
                    if (AudioManager != null)
                        WorkActionDriver.EmitCompletionSfx(AudioManager.SfxRouter);
                    string xpEv = WorkActionDriver.LastXpEvent;
                    if (xpEv.Length == 0)
                        xpEv = V.Str(res.Get("xp_event", ""));
                    string tid = WorkActionDriver.Work != null ? WorkActionDriver.Work.TargetId : "";
                    if (xpEv.Length > 0)
                        EmitTrainingEvent(xpEv, tid);
                    WorkActionDriver.Work?.Reset();
                }
            }
            RefreshWorkActionHud();
        }

        /// <summary>Mirror WorkAction yields into InventoryState; a cart overload spawns a floor WorkYieldDrop instead.</summary>
        void ApplyWorkYieldsToInventoryState(GdDict res)
        {
            if (InventoryState == null || res == null || res.IsEmpty)
                return;
            if (!V.Bool(res.Get("yields_applied", true)))
            {
                var pending = new GdDict();
                if (WorkActionDriver != null && !WorkActionDriver.PendingYields.IsEmpty)
                    pending = WorkActionDriver.PendingYields.DeepCopy();
                else if (res.Get("yields", null) is GdDict y)
                    pending = y.DeepCopy();
                if (!pending.IsEmpty)
                    SpawnWorkYieldDrop(pending);
                return;
            }
            if (!(res.Get("yields", null) is GdDict yields))
                return;
            foreach (object itemId in yields.Keys)
            {
                long qty = V.I64(yields[itemId]);
                if (qty > 0)
                    InventoryState.AddItem(V.Str(itemId), qty);
            }
            if (res.Get("consumed", null) is GdDict consumed)
            {
                foreach (object cid in consumed.Keys)
                {
                    long need = V.I64(consumed[cid]);
                    if (need <= 0)
                        continue;
                    string key = V.Str(cid);
                    if (InventoryState.GetQuantity(key) >= need)
                    {
                        InventoryState.RemoveItem(key, need);
                    }
                    else if (key == "hull_plate")
                    {
                        foreach (string alt in new[] { "plating_plate", "hull_plate_kit" })
                        {
                            if (InventoryState.GetQuantity(alt) >= need)
                            {
                                InventoryState.RemoveItem(alt, need);
                                break;
                            }
                        }
                    }
                }
            }
        }

        void SpawnWorkYieldDrop(GdDict items)
        {
            if (items.IsEmpty || !HasPlayer)
                return;
            string dropId = "work_yield_" + Clock.TicksMsec();
            var drop = new WorkYieldDrop();
            Vec3 pos = PlayerPos + new Vec3(0.6f, 0.0f, 0.4f);
            IShipSceneRoot parent = null;
            if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
            {
                parent = CurrentShip.SceneRoot;
                pos = ToLocal(CurrentShip.SceneRoot, pos);
            }
            drop.Configure(dropId, items, InventoryState, pos, 1.8);
            drop.Parent = parent;
            drop.Scooped += OnWorkYieldScooped;
            WorkYieldDrops.Add(Spawn(drop));
            PlaySfx(AudioEventSeam.SFX_DROP_ITEM, PlayerPos);
            if (WorkActionDriver != null)
            {
                WorkActionDriver.PendingYields.Clear();
                WorkActionDriver.Overloaded = false;
            }
        }

        void OnWorkYieldScooped(string dropId, GdDict granted)
        {
            var kept = new List<WorkYieldDrop>();
            foreach (WorkYieldDrop d in WorkYieldDrops)
            {
                if (!d.IsValid)
                    continue;
                if (d.DropId == dropId && d.ScoopedFlag)
                    continue;
                kept.Add(d);
            }
            WorkYieldDrops.Clear();
            WorkYieldDrops.AddRange(kept);
            PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
        }

        void InterruptWorkOnDamage()
        {
            if (WorkActionDriver == null || !WorkActionDriver.IsWorking())
                return;
            WorkActionDriver.Work?.Interrupt();
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            RefreshWorkActionHud();
        }

        // ------------------------------------------------------------------ module integrity scene consequences
        void ApplyModuleIntegrityStateToScene()
        {
            if (ModuleIntegrityMap == null)
                return;
            ApplyModuleIntegrityScene(GdString.ToGdArray(ModuleIntegrityMap.ModuleIds()));
        }

        /// <summary>Visual + collider consequences for changed modules on the active root (IModuleSceneView port).</summary>
        void ApplyModuleIntegrityScene(GdArray moduleIds)
        {
            IShipLoaderView root = null;
            if (AwayFromStart && CurrentShip != null && CurrentShip.SceneRoot is IShipLoaderView cr && cr.IsValid)
                root = cr;
            else if (Loader != null && Loader.IsValid)
                root = Loader;
            if (root == null)
                return;
            IReadOnlyList<IStructuralModuleNode> nodes = root.StructuralModuleNodes();
            foreach (object midV in moduleIds)
            {
                string mid = V.Str(midV);
                string st = ModuleIntegrityMap.GetState(mid);
                IStructuralModuleNode node = FindStructuralModuleNode(nodes, mid);
                if (node != null)
                {
                    IntegrityVisualResolver.ApplyVisualState(node, st);
                    ModuleIntegrityConsequences.ApplyToNode(node, st);
                }
            }
        }

        static IStructuralModuleNode FindStructuralModuleNode(IReadOnlyList<IStructuralModuleNode> nodes, string moduleKey)
        {
            if (string.IsNullOrEmpty(moduleKey))
                return null;
            foreach (IStructuralModuleNode n in nodes)
            {
                if ((n.HasModuleKeyMeta && n.ModuleKey == moduleKey) || n.StructuralPlacementId == moduleKey)
                    return n;
            }
            List<string> parts = GdString.Split(moduleKey, "/");
            string expected = parts.Count < 2 ? moduleKey : parts[0] + "_" + parts[1];
            foreach (IStructuralModuleNode n in nodes)
            {
                if (n.NodeName == expected)
                    return n;
            }
            return null;
        }

        Vec3 CompiledWrapperWorldPosition(string moduleKey)
        {
            if (string.IsNullOrEmpty(moduleKey))
                return Vec3.Inf;
            IShipLoaderView root = null;
            if (CurrentShip != null && CurrentShip.SceneRoot is IShipLoaderView cr && cr.IsValid)
                root = cr;
            else if (Loader != null && Loader.IsValid)
                root = Loader;
            if (root == null)
                return Vec3.Inf;
            IStructuralModuleNode node = FindStructuralModuleNode(root.StructuralModuleNodes(), moduleKey);
            return node != null ? node.GlobalPosition : Vec3.Inf;
        }

        // ------------------------------------------------------------------ ship modification
        /// <summary>A ship-mod install: mirror the panel bag into InventoryState, restore the linked sub, tiers, plating.</summary>
        public void OnShipModInstalled(string componentId, string itemForm)
        {
            if (InventoryState != null && !string.IsNullOrEmpty(itemForm) && InventoryState.GetQuantity(itemForm) > 0)
                InventoryState.RemoveItem(itemForm, 1);
            ApplyShipModSystemLink(componentId, true);
            RefreshStationTiersFromShipMod();
            ApplyShipModPlatingRepair(componentId);
            EmitTrainingEvent("ship_mod_install", componentId);
            PlaySfx(AudioEventSeam.UI_SHIP_MOD_INSTALL);
        }

        /// <summary>A ship-mod uninstall: add back returned items (panel bag minus live inventory), strip the linked sub.</summary>
        public void OnShipModUninstalled(string componentId, GdDict panelBag)
        {
            if (InventoryState != null && panelBag != null)
            {
                foreach (object itemId in panelBag.Keys)
                {
                    long bagQ = V.I64(panelBag[itemId]);
                    long liveQ = InventoryState.GetQuantity(V.Str(itemId));
                    if (bagQ > liveQ)
                        InventoryState.AddItem(V.Str(itemId), bagQ - liveQ);
                }
            }
            ApplyShipModSystemLink(componentId, false);
            RefreshStationTiersFromShipMod();
            EmitTrainingEvent("ship_mod_uninstall", componentId);
            PlaySfx(AudioEventSeam.UI_SHIP_MOD_UNINSTALL);
        }

        void ApplyShipModSystemLink(string componentId, bool installing)
        {
            if (string.IsNullOrEmpty(componentId) || ComponentCatalog == null || ShipSystemsManager == null)
                return;
            GdDict def = ComponentCatalog.GetComponent(componentId);
            if (def.IsEmpty)
                return;
            string sysId = V.Str(def.Get("linked_system", ""));
            string subId = V.Str(def.Get("linked_subcomponent", ""));
            if (sysId.Length == 0 || subId.Length == 0)
                return;
            if (installing)
                ShipSystemsManager.RestoreSubcomponentOnRemount(sysId, subId, 0.55);
            else
                ShipSystemsManager.DamageSubcomponent(sysId, subId, 0.6);
        }

        void ApplyShipModPlatingRepair(string componentId)
        {
            if (string.IsNullOrEmpty(componentId) || ComponentCatalog == null || ModuleIntegrityMap == null)
                return;
            GdDict def = ComponentCatalog.GetComponent(componentId);
            bool isPlating = V.Bool(def.Get("plating", false));
            if (!isPlating)
            {
                string formHint = componentId.ToLowerInvariant();
                isPlating = GdString.Find(formHint, "plating") >= 0 || GdString.Find(formHint, "plate") >= 0;
            }
            if (!isPlating)
                return;
            foreach (string mid in ModuleIntegrityMap.ModuleIds())
            {
                string st = ModuleIntegrityMap.GetState(mid);
                if (st != "damaged" && st != "breached")
                    continue;
                ModuleIntegrityState m = ModuleIntegrityMap.GetModule(mid);
                if (m != null)
                {
                    m.Repair(0.15);
                    ApplyModuleIntegrityScene(GdArray.Of(mid));
                    return;
                }
            }
        }

        /// <summary>After save/load: restore linked hub subs + station tiers from the ship-mod manifest.</summary>
        void ReapplyShipModRuntimeEffects()
        {
            if (ShipModificationState == null)
                return;
            foreach (object e in ShipModificationState.Installed)
            {
                if (!(e is GdDict entry))
                    continue;
                string cid = V.Str(entry.Get("component_id", ""));
                if (cid.Length > 0)
                    ApplyShipModSystemLink(cid, true);
            }
            RefreshStationTiersFromShipMod();
        }

        /// <summary>REQ-SMOD-001: ship-mod installs + physical placement feed station tiers.</summary>
        void RefreshStationTiersFromShipMod()
        {
            if (CraftingState == null)
                return;
            var placed = new GdArray();
            if (ShipModificationState != null)
            {
                foreach (object e in ShipModificationState.Installed)
                {
                    if (!(e is GdDict entry))
                        continue;
                    GdDict row = entry.DeepCopy();
                    row["mounted"] = true;
                    placed.Add(row);
                }
            }
            if (ComponentPlacementState != null)
            {
                foreach (object e in ComponentPlacementState.Placed)
                {
                    if (e is GdDict entry)
                        placed.Add(entry.DeepCopy());
                }
            }
            foreach (string kind in CRAFTING_STATION_KINDS)
            {
                if (kind == "salvage")
                    continue;
                CraftingState.RefreshStationTier(kind, placed, ComponentCatalog);
            }
        }
    }
}
