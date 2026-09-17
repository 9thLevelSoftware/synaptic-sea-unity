// Ported from scripts/tools/production_station.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A spatial, range-gated production station bound to one stateful production model (HydroponicsState or
    /// WaterRecyclerState) on the home ship. The first interact STARTS production (consuming inputs), a later
    /// interact HARVESTS once the model reports ready. Hydroponics IDLE interact opens the crop picker (REQ-CS-018).
    /// The coordinator ticks the model per-frame; this node only starts and collects.
    /// </summary>
    public sealed class ProductionStation : SessionInteractable
    {
        public override string Kind => "production_station";

        /// <summary>signal production_started(station_kind, input_id)</summary>
        public event Action<string, string> ProductionStarted;
        /// <summary>signal production_harvested(station_kind, item_id, qty)</summary>
        public event Action<string, string, long> ProductionHarvested;
        /// <summary>signal production_blocked(station_kind, reason)</summary>
        public event Action<string, string> ProductionBlocked;
        /// <summary>signal crop_picker_requested(station_kind) — REQ-CS-018.</summary>
        public event Action<string> CropPickerRequested;

        public string StationKind = "";
        /// <summary>HydroponicsState | WaterRecyclerState.</summary>
        public object Model;
        public InventoryState InventoryState;
        /// <summary><c>power_available</c> Callable: () -> float.</summary>
        public Func<double> PowerAvailable;
        /// <summary><c>player_skill</c> Callable: () -> int.</summary>
        public Func<long> PlayerSkill;
        /// <summary>hydroponics: {"crops": [...]}</summary>
        public GdDict Config = new GdDict();

        public void Configure(string stationKind, object model, InventoryState inventoryState, Func<double> powerAvailable, Func<long> playerSkill, GdDict config, Vec3 worldPosition, double radius = 1.8)
        {
            Debug.Assert(model != null, "p_model must not be null");
            Debug.Assert(inventoryState != null, "p_inventory_state must not be null");
            Debug.Assert(radius >= 0.0, "radius must be non-negative");
            StationKind = stationKind;
            Model = model;
            InventoryState = inventoryState;
            PowerAvailable = powerAvailable;
            PlayerSkill = playerSkill;
            Config = config ?? new GdDict();
            InteractionRadius = radius;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "ProductionStation_" + stationKind;
            // RUNTIME: set_meta("production_station", true), set_meta("station_kind", ...); sphere collision (radius);
            // box marker (radius*0.5 cube, 0.35,0.85,0.45,0.7 unshaded, no shadow), always visible.
        }

        double AvailPower() => PowerAvailable != null ? PowerAvailable() : 0.0;

        long Skill() => PlayerSkill != null ? PlayerSkill() : 0;

        /// <summary><c>model.state</c> of either production model.</summary>
        long ModelState()
        {
            switch (Model)
            {
                case HydroponicsState h: return h.CurrentState;
                case WaterRecyclerState w: return w.CurrentState;
                default: throw new InvalidOperationException("ProductionStation.Model must be HydroponicsState or WaterRecyclerState");
            }
        }

        /// <summary>Range-gated interact. Returns true when it opened the crop picker, started, or harvested.</summary>
        public bool TryInteract(Vec3 playerPosition)
        {
            if (Model == null || InventoryState == null)
                return false;
            if (!IsPlayerInDirectRange(playerPosition))
                return false;
            if (StationKind == "hydroponics")
                return InteractHydro();
            if (StationKind == "water_recycler")
                return InteractRecycler();
            ProductionBlocked?.Invoke(StationKind, "unknown_kind");
            return true; // consume — misconfigured station should not fall through
        }

        bool InteractHydro()
        {
            var model = (HydroponicsState)Model;
            if (model.CurrentState == (long)HydroponicsState.State.HARVESTABLE)
            {
                // Guard BEFORE harvest(): harvest() resets the model to IDLE, so check capacity first and leave
                // the crop HARVESTABLE so the player can free space and retry.
                if (!InventoryState.CanAccept(model.ProduceItemId, model.ProduceQuantity))
                {
                    ProductionBlocked?.Invoke(StationKind, "output_full");
                    return true; // consume — leave crop harvestable for retry
                }
                GdDict outD = model.Harvest();
                return Deposit(V.Str(outD.Get("item_id", "")), V.I64(outD.Get("quantity", 0L)));
            }
            if (model.CurrentState == (long)HydroponicsState.State.PLANTED)
            {
                // Growing crop — soft block + consume interact.
                ProductionBlocked?.Invoke(StationKind, "in_progress");
                return true;
            }
            // IDLE -> open crop picker (REQ-CS-018); no auto-plant.
            CropPickerRequested?.Invoke(StationKind);
            return true;
        }

        /// <summary>REQ-CS-018: list crop catalog rows for the picker (shape matches craft list entries).</summary>
        public GdArray ListCropEntries()
        {
            var outArr = new GdArray();
            if (StationKind != "hydroponics" || InventoryState == null)
                return outArr;
            GdArray crops = Config.Get("crops", new GdArray()) as GdArray ?? new GdArray();
            long skill = Skill();
            double power = AvailPower();
            double water = (double)InventoryState.GetQuantity("purified_water");
            // Sort by crop_id for deterministic picker order.
            var sortedCrops = new List<object>(crops);
            GdSort.SortCustom(sortedCrops, (a, b) =>
                GdString.Less(V.Str(((GdDict)a).Get("crop_id", "")), V.Str(((GdDict)b).Get("crop_id", ""))));
            foreach (object crop in sortedCrops)
            {
                if (!(crop is GdDict c))
                    continue;
                string cid = V.Str(c.Get("crop_id", ""));
                if (cid.Length == 0)
                    continue;
                long needSkill = V.I64(c.Get("required_skill_level", 0L));
                double waterCost = V.F64(c.Get("water_cost", 0.0));
                double powerCost = V.F64(c.Get("power_cost", 0.0));
                string produceId = V.Str(c.Get("produce_item_id", ""));
                long produceQty = V.I64(c.Get("produce_quantity", 0L));
                string status = "ready";
                if (Model != null && ModelState() != (long)HydroponicsState.State.IDLE)
                    status = "busy";
                else if (needSkill > skill)
                    status = "insufficient_skill";
                else if (water < waterCost)
                    status = "missing_ingredients";
                else if (power < powerCost)
                    status = "insufficient_power";
                outArr.Add(new GdDict
                {
                    { "recipe_id", cid },
                    { "display_name", V.Str(c.Get("display_name", cid)) },
                    { "category", "hydroponics" },
                    { "required_skill_level", needSkill },
                    { "ingredients", new GdDict { { "purified_water", (long)Math.Ceiling(waterCost) } } },
                    { "produces", new GdDict { { "item_id", produceId }, { "quantity", produceQty } } },
                    { "craft_time_seconds", V.F64(c.Get("growth_seconds", 0.0)) },
                    { "status", status },
                    { "craftable", status == "ready" },
                    { "crop_config", c.DeepCopy() },
                });
            }
            return outArr;
        }

        public string FirstReadyCropId()
        {
            foreach (object entry in ListCropEntries())
            {
                if (entry is GdDict d && V.Bool(d.Get("craftable", false)))
                    return V.Str(d.Get("recipe_id", ""));
            }
            return "";
        }

        GdDict FindCropConfig(string cropId)
        {
            GdArray crops = Config.Get("crops", new GdArray()) as GdArray ?? new GdArray();
            foreach (object crop in crops)
            {
                if (crop is GdDict c && V.Str(c.Get("crop_id", "")) == cropId)
                    return c.DeepCopy();
            }
            return new GdDict();
        }

        /// <summary>REQ-CS-018: plant a chosen crop_id (picker confirm + validation seams).</summary>
        public bool TryPlantCrop(string cropId)
        {
            if (StationKind != "hydroponics" || Model == null || InventoryState == null)
            {
                ProductionBlocked?.Invoke(StationKind, "not_hydro");
                return false;
            }
            if (string.IsNullOrEmpty(cropId))
            {
                ProductionBlocked?.Invoke(StationKind, "no_crop");
                return false;
            }
            var model = (HydroponicsState)Model;
            if (model.CurrentState != (long)HydroponicsState.State.IDLE)
            {
                ProductionBlocked?.Invoke(StationKind, model.CurrentState == (long)HydroponicsState.State.PLANTED ? "in_progress" : "busy");
                return false; // false = plant did not start
            }
            GdDict c = FindCropConfig(cropId);
            if (c.IsEmpty)
            {
                ProductionBlocked?.Invoke(StationKind, "unknown_crop");
                return false;
            }
            double waterCost = V.F64(c.Get("water_cost", 0.0));
            long skill = Skill();
            double power = AvailPower();
            if (V.I64(c.Get("required_skill_level", 0L)) > skill)
            {
                ProductionBlocked?.Invoke(StationKind, "insufficient_skill");
                return false;
            }
            if ((double)InventoryState.GetQuantity("purified_water") < waterCost)
            {
                ProductionBlocked?.Invoke(StationKind, "missing_ingredients");
                return false;
            }
            if (power < V.F64(c.Get("power_cost", 0.0)))
            {
                ProductionBlocked?.Invoke(StationKind, "insufficient_power");
                return false;
            }
            GdDict res = model.Plant(c, skill, (double)InventoryState.GetQuantity("purified_water"), power);
            if (V.Truthy(res.Get("ok", false)))
            {
                InventoryState.RemoveItem("purified_water", (long)Math.Ceiling(waterCost));
                ProductionStarted?.Invoke(StationKind, cropId);
                return true;
            }
            ProductionBlocked?.Invoke(StationKind, V.Str(res.Get("reason", "plant_failed")));
            return false;
        }

        bool InteractRecycler()
        {
            var model = (WaterRecyclerState)Model;
            if (model.OutputReady > 0)
            {
                // Guard BEFORE collect_output(): it clears output_ready, so check capacity first and leave the
                // output ready for a retry.
                if (!InventoryState.CanAccept(model.OutputItemId, model.OutputReady))
                {
                    ProductionBlocked?.Invoke(StationKind, "output_full");
                    return true; // consume — leave output ready for retry
                }
                GdDict outD = model.CollectOutput();
                return Deposit(V.Str(outD.Get("item_id", "")), V.I64(outD.Get("quantity", 0L)));
            }
            if (model.CurrentState == (long)WaterRecyclerState.State.RECYCLING)
            {
                // Recycling in progress — soft block + consume interact.
                ProductionBlocked?.Invoke(StationKind, "in_progress");
                return true;
            }
            // IDLE -> load contaminated_water.
            long qty = InventoryState.GetQuantity("contaminated_water");
            if (qty <= 0)
            {
                ProductionBlocked?.Invoke(StationKind, "no_input");
                return true; // consume — soft deny at the station
            }
            double power = AvailPower();
            if (power < model.PowerCost)
            {
                ProductionBlocked?.Invoke(StationKind, "insufficient_power");
                return true;
            }
            GdDict res = model.LoadInput("contaminated_water", qty, power);
            if (V.Truthy(res.Get("ok", false)))
            {
                InventoryState.RemoveItem("contaminated_water", qty);
                ProductionStarted?.Invoke(StationKind, "contaminated_water");
                return true;
            }
            ProductionBlocked?.Invoke(StationKind, V.Str(res.Get("reason", "load_failed")));
            return true;
        }

        bool Deposit(string itemId, long qty)
        {
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return false;
            long added = InventoryState.AddItem(itemId, qty);
            // (Godot printed "PRODUCTION OVERFLOW item=%s lost=%d reason=stack_full" when added < qty.)
            ProductionHarvested?.Invoke(StationKind, itemId, added);
            return true;
        }

        /// <summary>
        /// Headless validation injects candidate_player to bypass the spatial gate; otherwise the strict gate.
        /// </summary>
        bool IsPlayerInDirectRange(Vec3 playerPosition)
        {
            if (CandidatePlayerInRange)
                return true;
            return IsPlayerInDirectRangeStrict(playerPosition);
        }
    }
}
