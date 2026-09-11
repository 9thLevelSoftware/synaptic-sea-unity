// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: repair + breach-seal points (3107-3256), crafting and
// production stations (5394-5486), the repair/craft/production handlers (5564-5862), the recipe picker seams (6906-6996)
// and the crafting validation verbs (5872-6030).
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // ------------------------------------------------------------------ repair points
        /// <summary>One repair point per damaged subcomponent of the active ship, distributed across its rooms.</summary>
        void BuildRepairPoints()
        {
            ClearRepairPoints();
            ShipSystemsManager mgr = ActiveSystemsManager();
            if (mgr == null)
                return;
            bool useLifeboat = !AwayFromStart && LifeboatShip != null && RootValid(LifeboatShip.SceneRoot);
            List<Vec3> positions = useLifeboat ? LifeboatLocalRepairPositions() : DistributedRoomPositions();
            if (positions.Count == 0)
                return;
            int idx = 0;
            foreach (string sid in mgr.SystemOrder)
            {
                ShipSystem system = mgr.GetSystem(sid);
                if (system == null)
                    continue;
                foreach (ShipSubcomponent sub in system.Subcomponents)
                {
                    if (sub.IsFunctional())
                        continue;
                    Vec3 pos = positions[idx % positions.Count];
                    idx += 1;
                    var rp = new RepairPoint();
                    rp.Configure(sid, sub.SubcomponentId, mgr, InventoryState, PlayerProgression, pos, sub.RepairSeconds, sub.MinSkill, 1.8);
                    rp.RepairCompleted += OnRepairCompleted;
                    rp.RepairBlocked += OnRepairBlocked;
                    rp.RepairStarted += OnRepairStarted;
                    if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                        rp.Parent = CurrentShip.SceneRoot;
                    else if (LifeboatShip != null && RootValid(LifeboatShip.SceneRoot))
                        rp.Parent = LifeboatShip.SceneRoot;
                    RepairPoints.Add(Spawn(rp));
                }
            }
        }

        void ClearRepairPoints()
        {
            foreach (RepairPoint rp in RepairPoints)
                Despawn(rp);
            RepairPoints.Clear();
        }

        void BuildBreachSealPoints()
        {
            ClearBreachSealPoints();
            HullIntegrityState hull = ActiveHull();
            if (hull == null)
                return;
            var breached = new List<string>();
            foreach (object cid in hull.Compartments.Keys)
            {
                if ((hull.Compartments[cid] as GdDict ?? new GdDict()).GetBool("breach_open"))
                    breached.Add(V.Str(cid));
            }
            if (breached.Count == 0)
                return;
            bool useLifeboat = !AwayFromStart && LifeboatShip != null && RootValid(LifeboatShip.SceneRoot);
            List<Vec3> positions = useLifeboat ? LifeboatLocalRepairPositions() : DistributedRoomPositions();
            if (positions.Count == 0)
                return;
            int idx = 0;
            foreach (string cid in breached)
            {
                Vec3 pos = positions[idx % positions.Count];
                idx += 1;
                var sp = new BreachSealPoint();
                sp.Configure(cid, hull, InventoryState, PlayerProgression, pos, 4.0, "hull_sealant", 1.0, 1.8);
                sp.BreachSealed += OnBreachSealed;
                sp.SealBlocked += OnSealBlocked;
                if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                    sp.Parent = CurrentShip.SceneRoot;
                else if (LifeboatShip != null && RootValid(LifeboatShip.SceneRoot))
                    sp.Parent = LifeboatShip.SceneRoot;
                BreachSealPoints.Add(Spawn(sp));
            }
        }

        void ClearBreachSealPoints()
        {
            foreach (BreachSealPoint sp in BreachSealPoints)
                Despawn(sp);
            BreachSealPoints.Clear();
        }

        void OnBreachSealed(string compartmentId)
        {
            SetHazardFeedbackLine("Breach sealed: " + compartmentId);
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            EmitTrainingEvent("weld_panel", compartmentId);
            EmitTrainingEvent("build_shelter", compartmentId);
            TriggerTutorial("breach_sealed", "any");
        }

        void OnSealBlocked(string compartmentId, string reason)
        {
            SetHazardFeedbackLine("Seal blocked (" + compartmentId + "): " + HazardBlockReasonText(reason));
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
        }

        void OnRepairStarted(string systemId, string subcomponentId)
        {
            EmitTrainingEvent("diagnose_fault", systemId + "." + subcomponentId);
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
        }

        void OnRepairCompleted(string systemId, string subcomponentId)
        {
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            TryUnlockAchievement("repair_consumed", systemId + "." + subcomponentId);
            EmitTrainingEvent("repair_subcomponent", systemId + "." + subcomponentId);
            PlaySfx(AudioEventSeam.SFX_REPAIR_COMPLETE);
            ShipSystemsManager mgr = ActiveSystemsManager();
            bool operational = mgr != null && mgr.IsOperational(systemId);
            Log.Info("REPAIR COMPLETED system=" + systemId + " sub=" + subcomponentId + " operational=" + (operational ? "true" : "false"));
        }

        void OnRepairBlocked(string systemId, string subcomponentId, string reason)
        {
            VitalsModel?.NotifyRepairBlocked(reason);
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            SetHazardFeedbackLine(string.IsNullOrEmpty(reason) ? "Repair blocked" : "Repair blocked: " + reason);
        }

        void OnVoiceLogPlayed(string entryId)
        {
            EmitTrainingEvent("decode_signal", entryId);
            PlaySfx(AudioEventSeam.VOICE_LOG_PLAY);
        }

        /// <summary>Stream F: medbay field surgery when health is critical (CraftingStation's surgery provider).</summary>
        public bool TryMedbaySurgery()
        {
            if (VitalsState == null)
                return false;
            if (VitalsState.Health >= SURGERY_HEALTH_THRESHOLD)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            if (InventoryState == null || InventoryState.GetQuantity("medical_gauze") <= 0)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            if (InventoryState.RemoveItem("medical_gauze", 1) != 1)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return false;
            }
            VitalsState.Health = System.Math.Min(100.0, VitalsState.Health + SURGERY_HEAL_AMOUNT);
            EmitTrainingEvent("perform_surgery", "medbay");
            EmitTrainingEvent("first_aid_ally", "medbay");
            PlaySfx(AudioEventSeam.SFX_WOUND_TREAT);
            RefreshPlayerVitals(0.0);
            RefreshInventoryHud();
            Log.Info("MEDBAY SURGERY health=" + GdString.FormatFixed(VitalsState.Health, 1));
            return true;
        }

        // ------------------------------------------------------------------ crafting stations
        void BuildCraftingStations()
        {
            ClearCraftingStations();
            if (AwayFromStart || HomeShip == null || !RootValid(HomeShip.SceneRoot))
                return;
            if (CraftingState == null)
                return;
            List<Vec3> positions = HomeLocalStationPositions();
            if (positions.Count == 0)
            {
                float y = (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR;
                for (int i = 0; i < CRAFTING_STATION_KINDS.Count; i++)
                    positions.Add(new Vec3(i * 2.0f, y, 0.0f));
            }
            int idx = 0;
            foreach (string kind in CRAFTING_STATION_KINDS)
            {
                Vec3 pos = positions[idx % positions.Count];
                idx += 1;
                var st = new CraftingStation();
                st.Configure(kind, CraftingState, MaterialState, InventoryState, DeconstructionResolver, PlayerProgression, pos, 1.8);
                st.SurgeryProvider = this;
                st.CraftStarted += OnCraftStarted;
                st.SalvageCompleted += OnSalvageCompleted;
                st.CraftBlocked += OnCraftBlocked;
                st.RecipePickerRequested += OnRecipePickerRequested;
                st.Parent = HomeShip.SceneRoot;
                CraftingStations.Add(Spawn(st));
            }
        }

        void ClearCraftingStations()
        {
            foreach (CraftingStation st in CraftingStations)
                Despawn(st);
            CraftingStations.Clear();
        }

        void BuildProductionStations()
        {
            ClearProductionStations();
            if (AwayFromStart || HomeShip == null || !RootValid(HomeShip.SceneRoot))
                return;
            if (HydroponicsState == null || WaterRecyclerState == null || InventoryState == null)
                return;
            GdDict cropsCfg = LoadJsonDict(HYDROPONICS_CROPS_CONFIG_PATH);
            List<Vec3> positions = HomeLocalStationPositions();
            float y = (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR;
            var specs = new (string kind, object model, GdDict config)[]
            {
                ("hydroponics", HydroponicsState, cropsCfg),
                ("water_recycler", WaterRecyclerState, new GdDict()),
            };
            double PowerCb()
            {
                double ratio = PowerGridState != null ? PowerGridState.GetAllocationRatio("sustenance") : 0.0;
                return ratio >= 0.5 ? 999.0 : 0.0;
            }
            long SkillCb() => PlayerProgression != null ? PlayerProgression.GetSkillLevel("fabrication") : 0;
            int idx = 0;
            foreach ((string kind, object model, GdDict config) in specs)
            {
                Vec3 pos = positions.Count > 0 ? positions[idx % positions.Count] : new Vec3(idx * 2.0f, y, 0.0f);
                idx += 1;
                var st = new ProductionStation();
                st.Configure(kind, model, InventoryState, PowerCb, SkillCb, config, pos, 1.8);
                st.ProductionStarted += OnProductionStarted;
                st.ProductionHarvested += OnProductionHarvested;
                st.ProductionBlocked += OnProductionBlocked;
                st.CropPickerRequested += OnCropPickerRequested;
                st.Parent = HomeShip.SceneRoot;
                ProductionStations.Add(Spawn(st));
            }
        }

        void ClearProductionStations()
        {
            foreach (ProductionStation st in ProductionStations)
                Despawn(st);
            ProductionStations.Clear();
        }

        void OnCraftStarted(string stationKind, string recipeId)
        {
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            Log.Info("CRAFT STARTED station=" + stationKind + " recipe=" + recipeId);
        }

        /// <summary>REQ-FC: register newly acquired food/drink with the spoilage tracker (idempotent).</summary>
        void RegisterFoodForSpoilage(string itemId)
        {
            if (SpoilageState == null || string.IsNullOrEmpty(itemId) || SpoilageState.HasFood(itemId))
                return;
            GdDict defs = ItemDefs.LoadDefinitions();
            if (!(defs.Get(itemId, null) is GdDict definition))
                return;
            string category = V.Str(definition.Get("category", ""));
            if (category == "food" || category == "drink")
                SpoilageState.AddFood(itemId, definition);
        }

        /// <summary>A station craft finished: collect the product into the player inventory and train the station skill.</summary>
        void OnCraftCompleted()
        {
            if (CraftingState == null)
                return;
            GdDict result = CraftingState.FinishCraft();
            string itemId = V.Str(result.Get("item_id", ""));
            long qty = V.I64(result.Get("quantity", 0L));
            if (itemId.Length == 0 || qty <= 0)
                return;
            if (InventoryState != null)
            {
                long added = InventoryState.AddItem(itemId, qty);
                if (added < qty)
                    Log.Info("CRAFT OVERFLOW item=" + itemId + " lost=" + (qty - added) + " reason=stack_full");
                RegisterFoodForSpoilage(itemId);
            }
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_CRAFT_COMPLETE);
            Log.Info("CRAFT COMPLETED item=" + itemId + " qty=" + qty + " quality=" + V.Str(result.Get("quality_tier", "standard")));
            string stationKind = V.Str(result.Get("station_kind", ""));
            string recipeId = V.Str(result.Get("recipe_id", ""));
            if (stationKind == "kitchen" || (stationKind == "synthesizer" && RecipeIsCooking(recipeId, itemId)))
                EmitTrainingEvent("cook_meal", itemId);
            else if (stationKind == "fabricator" || stationKind == "workbench")
                EmitTrainingEvent("fabricate_part", itemId);
            else if (stationKind == "medbay" && (GdString.Contains(recipeId, "stim") || GdString.Contains(itemId, "stim")))
                EmitTrainingEvent("compound_stimulant", itemId);
        }

        bool RecipeIsCooking(string recipeId, string itemId)
        {
            if (CraftingState != null && recipeId.Length > 0)
            {
                GdDict recipe = CraftingState.GetRecipe(recipeId);
                if (V.Str(recipe.Get("category", "")) == "cooking")
                    return true;
            }
            if (InventoryState != null && itemId.Length > 0)
            {
                string outCat = InventoryState.GetCategory(itemId);
                if (outCat == "food" || outCat == "drink")
                    return true;
            }
            return false;
        }

        /// <summary>An emergency field craft finished (same deposit path as station crafts; trains fabricate_part).</summary>
        void OnFieldCraftCompleted()
        {
            if (FieldCraftingState == null)
                return;
            GdDict result = FieldCraftingState.FinishCraft();
            string itemId = V.Str(result.Get("item_id", ""));
            long qty = V.I64(result.Get("quantity", 0L));
            if (itemId.Length == 0 || qty <= 0)
                return;
            if (InventoryState != null)
            {
                long added = InventoryState.AddItem(itemId, qty);
                if (added < qty)
                    Log.Info("FIELD CRAFT OVERFLOW item=" + itemId + " lost=" + (qty - added) + " reason=stack_full");
                RegisterFoodForSpoilage(itemId);
            }
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_CRAFT_COMPLETE);
            Log.Info("FIELD CRAFT COMPLETED item=" + itemId + " qty=" + qty + " quality=" + V.Str(result.Get("quality_tier", "standard")));
            EmitTrainingEvent("fabricate_part", itemId);
        }

        void OnSalvageCompleted(string itemId, GdDict yields)
        {
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_WORK_UNBOLT);
            string sourceJunk = V.Str(yields.Get("source_junk", ""));
            string xpTarget = sourceJunk.Length > 0 ? sourceJunk : itemId;
            EmitTrainingEvent("scavenge_container", xpTarget);
        }

        void OnCraftBlocked(string stationKind, string reason)
        {
            Log.Info("CRAFT BLOCKED station=" + stationKind + " reason=" + reason);
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
        }

        void OnProductionStarted(string stationKind, string inputId)
        {
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
        }

        void OnProductionHarvested(string stationKind, string itemId, long qty)
        {
            if (qty <= 0)
                return;
            RegisterFoodForSpoilage(itemId);
            RefreshInventoryHud();
            RecomputePlayerEncumbrance();
            PlaySfx(AudioEventSeam.SFX_WORK_HARVEST);
            if (stationKind == "hydroponics")
                EmitTrainingEvent("cook_meal", itemId);
        }

        void OnProductionBlocked(string stationKind, string reason)
        {
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            SetHazardFeedbackLine("Production blocked (" + stationKind + "): " + reason);
        }

        // ------------------------------------------------------------------ field craft + recipe picker
        bool UiRecipePickerOpen => Deps.UiState != null && Deps.UiState.RecipePickerOpen;
        bool UiScannerOpen => Deps.UiState != null && Deps.UiState.ScannerOpen;
        bool UiInventoryOpen => Deps.UiState != null && Deps.UiState.InventoryOpen;
        bool UiMenusClosed => Deps.UiState == null || Deps.UiState.MenusClosed;

        /// <summary>
        /// <c>_on_player_field_craft_requested</c> (KEY_C): opens the portable recipe picker (field_crafting); refuses
        /// while busy or while a panel/menu is open.
        /// </summary>
        public void RequestFieldCraft()
        {
            if (FieldCraftingState == null || InventoryState == null)
                return;
            if (FieldCraftingState.IsCrafting())
            {
                OnCraftBlocked("field_crafting", "busy");
                Log.Info("FIELD CRAFT BLOCKED reason=busy");
                return;
            }
            if (UiRecipePickerOpen)
                return;
            if (UiScannerOpen || UiInventoryOpen)
            {
                OnCraftBlocked("field_crafting", "ui_busy");
                Log.Info("FIELD CRAFT BLOCKED reason=ui_busy");
                return;
            }
            if (!UiMenusClosed)
            {
                OnCraftBlocked("field_crafting", "menu_open");
                Log.Info("FIELD CRAFT BLOCKED reason=menu_open");
                return;
            }
            Events.RaisePanelRequested("recipe_picker", new GdDict { { "station_kind", "field_crafting" } });
            FreezePlayerForPanel();
        }

        /// <summary>Explicit field craft for a chosen recipe (picker confirm + tests).</summary>
        public bool BeginFieldCraftRecipe(string recipeId)
        {
            if (string.IsNullOrEmpty(recipeId) || FieldCraftingState == null || InventoryState == null)
                return false;
            if (FieldCraftingState.IsCrafting())
            {
                OnCraftBlocked("field_crafting", "busy");
                return false;
            }
            if (!FieldCraftingState.CanCraft(recipeId, InventoryState))
            {
                OnCraftBlocked("field_crafting", "cannot_craft");
                return false;
            }
            GdDict produces = CraftingState != null ? CraftingState.GetProduces(recipeId) : new GdDict();
            if (!produces.IsEmpty && !InventoryState.CanAccept(V.Str(produces.Get("item_id", "")), V.I64(produces.Get("quantity", 0L))))
            {
                OnCraftBlocked("field_crafting", "inventory_full");
                return false;
            }
            long skill = PlayerProgression != null ? PlayerProgression.GetSkillLevel("fabrication") : 0;
            if (FieldCraftingState.BeginCraft(recipeId, InventoryState, MaterialState, skill))
            {
                RefreshInventoryHud();
                RecomputePlayerEncumbrance();
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
                Log.Info("FIELD CRAFT STARTED recipe=" + recipeId);
                return true;
            }
            OnCraftBlocked("field_crafting", "begin_failed");
            return false;
        }

        /// <summary>First ready field recipe without UI (was <c>field_craft_first_ready_for_validation</c>).</summary>
        public bool FieldCraftFirstReady()
        {
            if (FieldCraftingState == null || InventoryState == null)
                return false;
            string rid = FieldCraftingState.FirstReadyRecipeId(InventoryState);
            if (rid.Length == 0)
                return false;
            return BeginFieldCraftRecipe(rid);
        }

        void OnRecipePickerRequested(string stationKind) => OpenSharedRecipePicker(stationKind);
        void OnCropPickerRequested(string stationKind) => OpenSharedRecipePicker(stationKind);

        void OpenSharedRecipePicker(string stationKind)
        {
            if (UiScannerOpen || UiInventoryOpen)
            {
                OnCraftBlocked(stationKind, "ui_busy");
                return;
            }
            if (!UiMenusClosed)
            {
                OnCraftBlocked(stationKind, "menu_open");
                return;
            }
            bool wasOpen = UiRecipePickerOpen;
            Events.RaisePanelRequested("recipe_picker", new GdDict { { "station_kind", stationKind } });
            if (!wasOpen)
                PlaySfx(AudioEventSeam.UI_PANEL_OPEN);
            FreezePlayerForPanel();
        }

        /// <summary>REQ-CS-016/017/018: pure listing seam for the recipe picker.</summary>
        public GdArray ListStationRecipeEntries(string stationKind)
        {
            if (InventoryState == null)
                return new GdArray();
            if (stationKind == "field_crafting")
                return FieldCraftingState != null ? FieldCraftingState.ListRecipeEntries(InventoryState) : new GdArray();
            if (stationKind == "salvage")
                return DeconstructionResolver != null ? DeconstructionResolver.ListSalvageEntries(InventoryState) : new GdArray();
            if (stationKind == "hydroponics")
            {
                foreach (ProductionStation st in ProductionStations)
                {
                    if (st.IsValid && st.StationKind == "hydroponics")
                        return st.ListCropEntries();
                }
                return new GdArray();
            }
            if (CraftingState == null)
                return new GdArray();
            long skill = PlayerProgression != null ? PlayerProgression.GetSkillLevel("fabrication") : 0;
            return CraftingState.ListRecipeEntries(stationKind, InventoryState, skill);
        }

        /// <summary>REQ-CS-016/017/018: the picker confirm handler.</summary>
        public GdDict BeginCraftFromPicker(string stationKind, string recipeId)
        {
            GdDict Result(bool ok, string reason) => new GdDict { { "ok", ok }, { "reason", reason }, { "recipe_id", recipeId } };
            if (string.IsNullOrEmpty(recipeId) || string.IsNullOrEmpty(stationKind))
            {
                OnCraftBlocked(!string.IsNullOrEmpty(stationKind) ? stationKind : "unknown", "bad_args");
                return Result(false, "bad_args");
            }
            if (stationKind == "field_crafting")
            {
                if (FieldCraftingState != null && FieldCraftingState.IsCrafting())
                {
                    OnCraftBlocked(stationKind, "busy");
                    return Result(false, "busy");
                }
                if (BeginFieldCraftRecipe(recipeId))
                    return Result(true, "started");
                return Result(false, "begin_failed");
            }
            if (stationKind == "salvage")
            {
                foreach (CraftingStation st in CraftingStations)
                {
                    if (st.IsValid && st.StationKind == "salvage")
                    {
                        if (st.TrySalvageTarget(recipeId))
                            return Result(true, "salvaged");
                        OnCraftBlocked(stationKind, "salvage_failed");
                        return Result(false, "salvage_failed");
                    }
                }
                OnCraftBlocked(stationKind, "station_missing");
                return Result(false, "station_missing");
            }
            if (stationKind == "hydroponics")
            {
                foreach (ProductionStation st in ProductionStations)
                {
                    if (st.IsValid && st.StationKind == "hydroponics")
                    {
                        if (st.TryPlantCrop(recipeId))
                            return Result(true, "planted");
                        OnCraftBlocked(stationKind, "plant_failed");
                        return Result(false, "plant_failed");
                    }
                }
                OnCraftBlocked(stationKind, "station_missing");
                return Result(false, "station_missing");
            }
            if (CraftingState != null && CraftingState.IsCrafting())
            {
                OnCraftBlocked(stationKind, "busy");
                return Result(false, "busy");
            }
            foreach (CraftingStation st in CraftingStations)
            {
                if (st.IsValid && st.StationKind == stationKind)
                {
                    if (st.TryCraftRecipe(recipeId))
                        return Result(true, "started");
                    OnCraftBlocked(stationKind, "begin_failed");
                    return Result(false, "begin_failed");
                }
            }
            OnCraftBlocked(stationKind, "station_missing");
            return Result(false, "station_missing");
        }

        /// <summary>Start a repair channel through the real range gate (was <c>repair_subcomponent_for_validation</c>).</summary>
        public bool RepairSubcomponent(string systemId, string subcomponentId)
        {
            if (!HasPlayer)
                return false;
            foreach (RepairPoint rp in RepairPoints)
            {
                if (rp.IsValid && rp.SystemId == systemId && rp.SubcomponentId == subcomponentId && !rp.Repaired)
                {
                    TeleportPlayer(rp.GlobalPosition);
                    if (!rp.TryStart(PlayerPos))
                        return false;
                    return rp.Channeling;
                }
            }
            return false;
        }

        /// <summary>Pump every channeling repair point by delta (was <c>advance_repair_channels_for_validation</c>).</summary>
        public void AdvanceRepairChannels(double delta)
        {
            foreach (RepairPoint rp in new List<RepairPoint>(RepairPoints))
            {
                if (rp.IsValid && rp.Channeling)
                    rp.AdvanceChannel(delta);
            }
        }

        /// <summary>Station craft of the first ready recipe through the real path (was <c>craft_at_station_for_validation</c>).</summary>
        public bool CraftAtStation(string stationKind)
        {
            if (!HasPlayer)
                return false;
            foreach (CraftingStation st in CraftingStations)
            {
                if (st.IsValid && st.StationKind == stationKind)
                {
                    TeleportPlayer(st.GlobalPosition);
                    CraftingState?.GetOrCreateStation(stationKind).SetPower(true);
                    st.SetPowered(true);
                    string rid = st.FirstReadyRecipeId();
                    if (rid.Length == 0)
                        return false;
                    if (stationKind == "salvage")
                        return st.TrySalvageTarget(rid);
                    return st.TryCraftRecipe(rid);
                }
            }
            return false;
        }

        /// <summary>Advance the active station craft (+ field craft) by delta, depositing outputs (was <c>advance_crafting_for_validation</c>).</summary>
        public void AdvanceCrafting(double delta)
        {
            if (CraftingState != null)
            {
                string activeKind = CraftingState.GetActiveStationKind();
                if (activeKind.Length > 0)
                    CraftingState.GetOrCreateStation(activeKind).SetPower(true);
                if (CraftingState.Tick(delta))
                    OnCraftCompleted();
            }
            if (FieldCraftingState != null && FieldCraftingState.Tick(delta))
                OnFieldCraftCompleted();
        }

        /// <summary>Advance hydroponics + water recycler by delta (was <c>advance_production_for_validation</c>).</summary>
        public void AdvanceProduction(double delta)
        {
            if (HydroponicsState != null && HydroponicsState.CurrentState == (long)HydroponicsState.State.PLANTED)
                HydroponicsState.Tick(delta);
            if (WaterRecyclerState != null && WaterRecyclerState.CurrentState == (long)WaterRecyclerState.State.RECYCLING)
                WaterRecyclerState.Tick(delta);
        }
    }
}
