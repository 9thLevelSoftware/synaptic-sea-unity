using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Diagnostic generations retain the complete configured raw manager, including owners without canonical
    // component machinery links. Ordinary summary parsers and their health tolerance remain unchanged.
    internal static class ComponentRawShipSystems
    {
        internal static bool Validate(object value)
        {
            var configured = new ShipSystemsManager();
            configured.Configure(configured.LoadDefinitions(), ShipSystemsManager.CONDITION_PRISTINE, 0);
            return Validate(value, configured);
        }

        static bool Validate(object value, ShipSystemsManager configured)
        {
            if (configured == null || configured.Systems.Count == 0 || !(value is GdDict manager) ||
                !(manager.Get("systems") is GdDict systems) || !(manager.Get("system_order") is GdArray order) ||
                systems.Count != configured.Systems.Count || order.Count != configured.SystemOrder.Count) return false;
            for (int i = 0; i < order.Count; i++)
                if (!(order[i] is string id) || id != configured.SystemOrder[i]) return false;
            foreach (var pair in configured.Systems)
            {
                if (!(systems.Get(pair.Key) is GdDict row) || !(row.Get("system_id") is string id) || id != pair.Key ||
                    !(row.Get("dependency_ids") is GdArray dependencies) || dependencies.Count != pair.Value.DependencyIds.Count ||
                    !(row.Get("subcomponents") is GdArray subs) || subs.Count != pair.Value.Subcomponents.Count) return false;
                for (int i = 0; i < dependencies.Count; i++)
                    if (!(dependencies[i] is string dependency) || dependency != pair.Value.DependencyIds[i]) return false;
                GdDict shape = pair.Value.GetSummary();
                if (row.Count != shape.Count || shape.Keys.Any(k => !row.Has(k)) ||
                    pair.Value is LifeSupportSystem && !(row.Get("oxygen") is GdDict)) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object item in subs)
                {
                    if (!(item is GdDict sub) || !(sub.Get("subcomponent_id") is string subId) || !seen.Add(subId) ||
                        !Finite(sub.Get("health"), out double health) || health < 0 || health > 1) return false;
                    ShipSubcomponent expected = pair.Value.GetSubcomponent(subId);
                    if (expected == null) return false;
                    GdDict staticShape = expected.GetSummary();
                    if (sub.Count != staticShape.Count) return false;
                    foreach (var field in staticShape)
                        if (!sub.Has(field.Key) || field.Key as string != "health" && !SameWire(field.Value, sub.Get(field.Key))) return false;
                }
            }
            return true;
        }

        // JSON mirrors use their ordinary finite wire precision; this never changes the canonical owner codec.
        static bool SameWire(object expected, object actual)
            => SaveCommitCoordinator.SameComponentMirror(expected, actual);
        static bool Finite(object value, out double number)
        {
            number = V.F64(value, double.NaN);
            return V.IsNumber(value) && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        internal static void ApplyExactHealth(ShipSystemsManager manager, object value)
        {
            if (!Validate(value, manager)) throw new InvalidOperationException("component_raw_systems_mismatch");
            var systems = (GdDict)((GdDict)value).Get("systems");
            foreach (var pair in manager.Systems)
                foreach (GdDict sub in (GdArray)((GdDict)systems.Get(pair.Key)).Get("subcomponents"))
                    pair.Value.GetSubcomponent(sub.GetString("subcomponent_id")).Health = V.F64(sub.Get("health"));
        }
    }
}
