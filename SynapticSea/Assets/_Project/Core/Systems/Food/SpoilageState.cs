// Ported from scripts/systems/spoilage_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Aggregated spoilage tracker for all food items in inventory or cargo. Owns item_id -> <see cref="FoodState"/>,
    /// ticks every food item, reports stage transitions, and exposes batch queries for UI/HUD. Pure model.
    /// Implements the <see cref="FoodTravelPlanner"/> spoilage surfaces (travel range, add/has food).
    /// </summary>
    /// <remarks>
    /// The GDScript <c>foods</c> Dictionary holds FoodState objects, which a <see cref="GdDict"/> cannot, so it is an
    /// insertion-ordered map here (same iteration order as the Godot Dictionary, including after erase).
    /// </remarks>
    public sealed class SpoilageState : ISimModel, IStatusLineProvider,
        FoodTravelPlanner.ITravelRangeSource, FoodTravelPlanner.IFoodRegistry, FoodTravelPlanner.IFoodLookup
    {
        readonly List<string> _foodOrder = new List<string>();
        readonly Dictionary<string, FoodState> _foods = new Dictionary<string, FoodState>(StringComparer.Ordinal);
        long _lastTransitionCount = 0;

        /// <summary>Tracked item ids in Godot Dictionary order.</summary>
        public IReadOnlyList<string> FoodIds => _foodOrder;

        public long FoodCount => _foodOrder.Count;

        /// <summary>SpoilageState has no <c>configure()</c> in GDScript; this resets like a fresh instance (used by <see cref="ISimModel"/>).</summary>
        void ISimModel.Configure(GdDict config) => Clear();

        public FoodState AddFood(string itemId, GdDict config)
        {
            var fs = new FoodState();
            GdDict cfg = config != null ? config.DeepCopy() : new GdDict();
            cfg["item_id"] = itemId;
            fs.Configure(cfg);
            SetFood(itemId, fs);
            return fs;
        }

        void FoodTravelPlanner.IFoodRegistry.AddFood(string itemId, GdDict config) => AddFood(itemId, config);

        public void RemoveFood(string itemId)
        {
            if (_foods.Remove(itemId)) _foodOrder.Remove(itemId);
        }

        public bool HasFood(string itemId) => itemId != null && _foods.ContainsKey(itemId);

        /// <summary>The tracked FoodState, or null.</summary>
        public FoodState GetFood(string itemId) => itemId != null && _foods.TryGetValue(itemId, out FoodState fs) ? fs : null;

        public long Tick(double deltaSeconds)
        {
            long transitions = 0;
            if (deltaSeconds <= 0.0) return 0;
            foreach (string itemId in _foodOrder)
            {
                if (_foods[itemId].Tick(deltaSeconds)) transitions += 1;
            }
            _lastTransitionCount = transitions;
            return transitions;
        }

        public long GetFoodCountByStage(long stage)
        {
            long count = 0;
            foreach (string itemId in _foodOrder)
            {
                if (_foods[itemId].CurrentStage == stage) count += 1;
            }
            return count;
        }

        public bool GetAnyRotten()
        {
            foreach (string itemId in _foodOrder)
            {
                if (_foods[itemId].CurrentStage == (long)FoodState.Stage.ROTTEN) return true;
            }
            return false;
        }

        /// <summary>
        /// PKG-C3.2 eat path: apply spoilage-scaled restores, then drop tracking. Each collaborator is optional and
        /// duck-typed in GDScript: <paramref name="inventory"/> is used when it is a
        /// <see cref="CargoTransfer.ICargoStore"/> (<c>get_quantity</c>/<c>remove_item</c>), <paramref name="vitals"/>
        /// when it is an <see cref="EffectDispatcher.IVitalsTarget"/>, and <paramref name="sanityState"/> when it is an
        /// <see cref="EffectDispatcher.ISanityTarget"/>.
        /// </summary>
        public GdDict Eat(string itemId, object inventory = null, object vitals = null, object sanityState = null)
        {
            var outDict = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "hunger", 0.0 },
                { "thirst", 0.0 },
                { "sanity", 0.0 },
                { "sickness_risk", 0.0 },
                { "stage", -1L },
            };
            if (string.IsNullOrEmpty(itemId) || !_foods.ContainsKey(itemId))
            {
                outDict["reason"] = "not_tracked";
                return outDict;
            }
            var store = inventory as CargoTransfer.ICargoStore;
            if (store != null)
            {
                if (store.GetQuantity(itemId) < 1)
                {
                    outDict["reason"] = "missing_inventory";
                    return outDict;
                }
            }
            FoodState fs = _foods[itemId];
            GdDict effect = fs.Consume();
            outDict["hunger"] = V.F64(effect.Get("hunger", 0.0));
            outDict["thirst"] = V.F64(effect.Get("thirst", 0.0));
            outDict["sanity"] = V.F64(effect.Get("sanity", 0.0));
            outDict["sickness_risk"] = V.F64(effect.Get("sickness_risk", 0.0));
            outDict["stage"] = fs.CurrentStage;
            if (vitals is EffectDispatcher.IVitalsTarget vitalsTarget)
                vitalsTarget.ApplyDelta(new GdDict { { "hunger", outDict["hunger"] }, { "thirst", outDict["thirst"] } });
            if (sanityState is EffectDispatcher.ISanityTarget sanityTarget && Math.Abs(V.F64(outDict["sanity"])) > 0.0)
                sanityTarget.AdjustSanity(V.F64(outDict["sanity"]));
            if (store != null) store.RemoveItem(itemId, 1);
            // If inventory still has stacks, keep spoilage tracking; else remove.
            if (store != null && store.GetQuantity(itemId) > 0)
            {
                // keep tracking
            }
            else
            {
                RemoveFood(itemId);
            }
            outDict["ok"] = true;
            return outDict;
        }

        /// <summary>PKG-C3.2: sum effective hunger restore across tracked foods (travel planning).</summary>
        public double TotalEffectiveHunger()
        {
            double total = 0.0;
            foreach (string itemId in _foodOrder)
            {
                GdDict eff = _foods[itemId].GetEffectiveRestores();
                total += V.F64(eff.Get("hunger", 0.0));
            }
            return total;
        }

        /// <summary>Estimated travel-days of food at a fixed hunger drain per day (default 24*0.5 from VitalsState).</summary>
        public double TravelRangeDays(double hungerDrainPerDay = 12.0)
        {
            if (hungerDrainPerDay <= 0.0) return 0.0;
            return TotalEffectiveHunger() / hungerDrainPerDay;
        }

        public GdDict GetSummary()
        {
            var entries = new GdDict();
            foreach (string itemId in _foodOrder) entries[itemId] = _foods[itemId].GetSummary();
            return new GdDict
            {
                { "foods", entries },
                { "transition_count", _lastTransitionCount },
                { "rotten_present", GetAnyRotten() },
            };
        }

        /// <summary>
        /// Restores foods by id. A food that is not yet tracked is restored into a bare FoodState via its own
        /// apply_summary, which (as in Godot) does not carry every field; see the fixture's round_trip note.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            object foodsVariant = summary.Get("foods", new GdDict());
            if (foodsVariant is GdDict foodsDict)
            {
                // Add / update existing
                foreach (var kv in foodsDict)
                {
                    if (!(kv.Value is GdDict foodSummary)) continue;
                    string itemId = V.Str(kv.Key);
                    if (_foods.TryGetValue(itemId, out FoodState existing))
                    {
                        if (existing.ApplySummary(foodSummary)) changed = true;
                    }
                    else
                    {
                        var fs = new FoodState();
                        fs.ApplySummary(foodSummary);
                        SetFood(itemId, fs);
                        changed = true;
                    }
                }
                // Remove foods no longer in summary
                var toRemove = new List<string>();
                foreach (string itemId in _foodOrder)
                {
                    if (!foodsDict.Has(itemId)) toRemove.Add(itemId);
                }
                foreach (string itemId in toRemove)
                {
                    RemoveFood(itemId);
                    changed = true;
                }
            }
            long restoredTransitionCount = V.I64(summary.Get("transition_count", 0L));
            if (_lastTransitionCount != restoredTransitionCount) changed = true;
            _lastTransitionCount = restoredTransitionCount;
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            long fresh = GetFoodCountByStage((long)FoodState.Stage.FRESH);
            long stale = GetFoodCountByStage((long)FoodState.Stage.STALE);
            long rotten = GetFoodCountByStage((long)FoodState.Stage.ROTTEN);
            lines.Add("Food stocks: Fresh=" + GdString.FormatInt(fresh) + " Stale=" + GdString.FormatInt(stale) + " Rotten=" + GdString.FormatInt(rotten));
            if (rotten > 0) lines.Add("WARNING: " + GdString.FormatInt(rotten) + " rotten item(s) present");
            return lines;
        }

        public void Clear()
        {
            _foods.Clear();
            _foodOrder.Clear();
            _lastTransitionCount = 0;
        }

        /// <summary><c>foods[item_id] = fs</c>: replaces in place when present, else appends.</summary>
        void SetFood(string itemId, FoodState fs)
        {
            if (!_foods.ContainsKey(itemId)) _foodOrder.Add(itemId);
            _foods[itemId] = fs;
        }
    }
}
