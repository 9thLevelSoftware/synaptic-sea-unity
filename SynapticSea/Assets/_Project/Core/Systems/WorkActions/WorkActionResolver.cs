// Ported from scripts/systems/work_action_resolver.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.2b: resolve a completed WorkAction against world models. Pure — returns a result dictionary for the
    /// scene layer to apply (inventory, audio, threat noise, training bus).
    /// </summary>
    public static class WorkActionResolver
    {
        /// <summary>
        /// Complete an active WorkActionState against an optional ModuleIntegrityMap. Returns:
        /// ok, verb, noise, xp_event, yields, consumed, module_id, module_state, atmosphere_link, nav_gap,
        /// crawl_passable, reason.
        /// </summary>
        public static GdDict ResolveCompletion(WorkActionState work, ModuleIntegrityMap moduleMap = null, string moduleId = "")
        {
            moduleId = moduleId ?? "";
            var output = new GdDict
            {
                { "ok", false },
                { "verb", "" },
                { "noise", 0.0 },
                { "xp_event", "" },
                { "yields", new GdDict() },
                { "consumed", new GdDict() },
                { "module_id", moduleId },
                { "module_state", "" },
                { "atmosphere_link", false },
                { "nav_gap", false },
                { "crawl_passable", false },
                { "reason", "" },
            };
            if (work == null)
            {
                output["reason"] = "no_work";
                return output;
            }
            string status = work.Status;
            if (status != WorkActionState.STATUS_COMPLETED)
            {
                output["reason"] = "not_completed";
                return output;
            }
            GdDict sum = work.GetSummary();
            GdDict def = sum.Get("definition", new GdDict()) as GdDict ?? new GdDict();
            output["verb"] = V.Str(def.Get("verb", ""));
            output["noise"] = work.Noise();
            output["xp_event"] = work.XpEvent();
            output["yields"] = work.MaterialsYielded();
            output["consumed"] = work.MaterialsConsumed();

            string verb = V.Str(output["verb"]);
            string targetKind = V.Str(def.Get("target_kind", "module"));
            if (moduleMap != null && moduleId.Length != 0 && (targetKind == "module" || targetKind == "breach"))
            {
                string kind = "";
                ModuleIntegrityState m = moduleMap.GetModule(moduleId);
                if (m != null)
                    kind = m.Kind;
                switch (verb)
                {
                    case "cut":
                    case "pry":
                        // Destroy / heavy damage the structural module.
                        moduleMap.ApplyDamage(moduleId, 1.0, kind);
                        break;
                    case "weld":
                    case "patch":
                        if (m != null)
                        {
                            m.Repair(0.35);
                        }
                        else
                        {
                            // ensure then repair
                            moduleMap.EnsureModule(moduleId, kind);
                            m = moduleMap.GetModule(moduleId);
                            if (m != null)
                                m.Repair(0.35);
                        }
                        break;
                }
                string st = moduleMap.GetState(moduleId);
                output["module_state"] = st;
                GdDict cons = ModuleIntegrityConsequences.ConsequenceForState(st);
                output["atmosphere_link"] = V.Bool(cons.Get("atmosphere_link", false));
                output["nav_gap"] = V.Bool(cons.Get("nav_gap", false));
                output["crawl_passable"] = V.Bool(cons.Get("crawl_passable", false));
            }

            output["ok"] = true;
            return output;
        }

        /// <summary>Apply yields into a simple inventory Dictionary (item_id -> qty). Mutates inventory.</summary>
        public static GdDict ApplyYieldsToInventory(GdDict inventory, GdDict yields)
        {
            foreach (object itemId in new List<object>(yields.Keys))
            {
                long add = V.I64(yields[itemId]);
                if (add == 0)
                    continue;
                string key = V.Str(itemId);
                inventory[key] = V.I64(inventory.Get(key, 0L)) + add;
            }
            return inventory;
        }

        /// <summary>Consume materials from inventory Dictionary. Returns false if insufficient.</summary>
        public static bool ConsumeFromInventory(GdDict inventory, GdDict consumed)
        {
            foreach (object itemId in consumed.Keys)
            {
                long need = V.I64(consumed[itemId]);
                string key = V.Str(itemId);
                if (V.I64(inventory.Get(key, 0L)) < need)
                    return false;
            }
            foreach (object itemId in new List<object>(consumed.Keys))
            {
                long need2 = V.I64(consumed[itemId]);
                string key2 = V.Str(itemId);
                inventory[key2] = V.I64(inventory.Get(key2, 0L)) - need2;
                if (V.I64(inventory[key2]) <= 0)
                    inventory.Erase(key2);
            }
            return true;
        }
    }
}
