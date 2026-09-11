using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Shared setup for batch-B tests that read the synced catalogs.</summary>
    internal static class ItemsTestData
    {
        public static void Use()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        public static void Reset() => CatalogRegistry.Clear();
    }

    /// <summary>Minimal StatusEffectsState stand-in (same add/remove rules as status_effects_state.gd).</summary>
    internal sealed class FakeStatusEffects : EffectDispatcher.IStatusEffectsTarget
    {
        public readonly GdArray Effects = new GdArray();

        public bool AddEffect(string effectId, double duration, long stacks = 1)
        {
            if (string.IsNullOrEmpty(effectId) || duration <= 0.0 || stacks <= 0) return false;
            foreach (object o in Effects)
            {
                var e = (GdDict)o;
                if (V.Str(e.Get("id", "")) == effectId)
                {
                    e["stacks"] = V.I64(e.Get("stacks", 0L)) + stacks;
                    e["duration"] = Math.Max(V.F64(e.Get("duration", 0.0)), duration);
                    return true;
                }
            }
            Effects.Add(new GdDict { { "id", effectId }, { "duration", duration }, { "stacks", stacks } });
            return true;
        }

        public bool RemoveEffect(string effectId, long stacks = 1)
        {
            for (int i = 0; i < Effects.Count; i++)
            {
                var e = (GdDict)Effects[i];
                if (V.Str(e.Get("id", "")) != effectId) continue;
                long current = V.I64(e.Get("stacks", 0L));
                if (current <= stacks) Effects.RemoveAt(i);
                else e["stacks"] = current - stacks;
                return true;
            }
            return false;
        }

        public bool HasEffect(string effectId)
        {
            foreach (object o in Effects)
                if (V.Str(((GdDict)o).Get("id", "")) == effectId) return true;
            return false;
        }

        public GdDict GetSummary() => new GdDict { { "effects", Effects.DeepCopy() } };
    }

    internal sealed class FakeVitals : EffectDispatcher.IVitalsTarget
    {
        public double Health = 100.0, Stamina = 100.0, Hunger = 100.0, Thirst = 100.0;

        public GdDict ApplyDelta(GdDict delta)
        {
            Health = GdMath.Clampf(Health + delta.GetFloat("health"), 0.0, 100.0);
            Stamina = GdMath.Clampf(Stamina + delta.GetFloat("stamina"), 0.0, 100.0);
            Hunger = GdMath.Clampf(Hunger + delta.GetFloat("hunger"), 0.0, 100.0);
            Thirst = GdMath.Clampf(Thirst + delta.GetFloat("thirst"), 0.0, 100.0);
            return GetSummary();
        }

        public GdDict GetSummary() =>
            new GdDict { { "health", Health }, { "stamina", Stamina }, { "hunger", Hunger }, { "thirst", Thirst } };
    }

    internal sealed class FakeSanity : EffectDispatcher.ISanityTarget
    {
        public double Sanity = 100.0;
        public double AdjustSanity(double amount) => Sanity = GdMath.Clampf(Sanity + amount, 0.0, 100.0);
        public GdDict GetSummary() => new GdDict { { "sanity", Sanity } };
    }

    internal sealed class FakeRadiation : EffectDispatcher.IRadiationTarget
    {
        public double Radiation;
        public double AdjustRadiation(double amount) => Radiation = GdMath.Clampf(Radiation + amount, 0.0, 100.0);
        public GdDict GetSummary() => new GdDict { { "radiation", Radiation } };
    }

    /// <summary>Weight-capped inventory standing in for InventoryState (uncapped) and ShipInventory (hard cap).</summary>
    internal sealed class FakeCargo : CargoTransfer.ICargoPlayer, CargoTransfer.ICargoHold
    {
        public readonly GdDict ItemMap = new GdDict();
        readonly Dictionary<string, (string category, double weight)> _defs;
        readonly double _maxWeight;

        public FakeCargo(Dictionary<string, (string, double)> defs, double maxWeight = double.PositiveInfinity)
        {
            _defs = defs;
            _maxWeight = maxWeight;
        }

        public GdDict Items => ItemMap;

        public string GetCategory(string itemId) => _defs.TryGetValue(itemId, out var d) ? d.category : "";

        public long GetQuantity(string itemId) => ItemMap.GetInt(itemId, 0);

        double TotalWeight()
        {
            double w = 0;
            foreach (var kv in ItemMap) w += V.I64(kv.Value) * Weight(V.Str(kv.Key));
            return w;
        }

        double Weight(string itemId) => _defs.TryGetValue(itemId, out var d) ? d.weight : 0.0;

        public long AddItem(string itemId, long qty)
        {
            double each = Weight(itemId);
            long accept = qty;
            if (each > 0 && !double.IsInfinity(_maxWeight))
                accept = Math.Min(qty, (long)Math.Floor((_maxWeight - TotalWeight()) / each));
            if (accept <= 0) return 0;
            ItemMap[itemId] = GetQuantity(itemId) + accept;
            return accept;
        }

        public long RemoveItem(string itemId, long qty)
        {
            long have = GetQuantity(itemId);
            long take = Math.Min(have, qty);
            if (take <= 0) return 0;
            if (have - take <= 0) ItemMap.Erase(itemId);
            else ItemMap[itemId] = have - take;
            return take;
        }

        public GdArray GetItemsByCategory(string category)
        {
            var outArr = new GdArray();
            foreach (var kv in ItemMap)
                if (GetCategory(V.Str(kv.Key)) == category)
                    outArr.Add(new GdDict { { "id", kv.Key }, { "quantity", kv.Value }, { "weight_each", Weight(V.Str(kv.Key)) } });
            return outArr;
        }
    }
}
