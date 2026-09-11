// Ported from scripts/systems/ship_modification_state.gd @ 96ecb2b0

using System;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-D2.6: pure hub/ship component install manifest + power budget gate. Installs consume inventory
    /// item_form and add to ship slots; power budget constraints bite when demand exceeds supply.
    /// Never touches the scene tree.
    /// </summary>
    public class ShipModificationState : ISimModel
    {
        public const string DEFAULT_BUDGET_PATH = "res://data/ship_systems/power_budget_tables.json";

        /// <summary>Array of {slot_id, component_id, item_form, power_draw, mass, source_ship, plating}.</summary>
        public GdArray Installed = new GdArray();
        public double PowerSupply = 100.0;
        public double PowerDemandBaseline = 0.0;
        public double MinOperationalRatio = 0.5;

        /// <summary>Integrity repair buffer from plating installs.</summary>
        public double HullPlatingBonus = 0.0;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            Installed.Clear();
            PowerSupply = Math.Max(0.0, V.F64(config.Get("power_supply", 100.0)));
            PowerDemandBaseline = Math.Max(0.0, V.F64(config.Get("power_demand_baseline", 0.0)));
            MinOperationalRatio = GdMath.Clampf(V.F64(config.Get("min_operational_ratio", 0.5)), 0.0, 1.0);
            HullPlatingBonus = Math.Max(0.0, V.F64(config.Get("hull_plating_bonus", 0.0)));
            object raw = config.Get("installed", new GdArray());
            if (raw is GdArray rawArr)
            {
                foreach (object e in rawArr)
                    if (e is GdDict entry)
                        Installed.Add(entry.DeepCopy());
            }
            if (config.Has("budget_path") || CatalogRegistry.Exists(DEFAULT_BUDGET_PATH))
                LoadBudget(V.Str(config.Get("budget_path", DEFAULT_BUDGET_PATH)));
        }

        void LoadBudget(string path)
        {
            if (!CatalogRegistry.Exists(path))
                return;
            if (!(CatalogRegistry.Load(path) is GdDict root))
                return;
            PowerSupply = Math.Max(0.0, V.F64(root.Get("total_supply_units", PowerSupply)));
            MinOperationalRatio = GdMath.Clampf(V.F64(root.Get("min_operational_ratio", MinOperationalRatio)), 0.0, 1.0);
            object demand = root.Get("baseline_demand_units", new GdDict());
            if (demand is GdDict demandDict)
            {
                double total = 0.0;
                foreach (object k in demandDict.Keys)
                    total += V.F64(demandDict[k]);
                PowerDemandBaseline = total;
            }
        }

        public long InstalledCount() => Installed.Count;

        public double TotalPowerDraw()
        {
            double draw = PowerDemandBaseline;
            foreach (object e in Installed)
            {
                if (!(e is GdDict entry))
                    continue;
                draw += V.F64(entry.Get("power_draw", 0.0));
            }
            return draw;
        }

        public double PowerRatio()
        {
            if (PowerSupply <= 0.0)
                return 0.0;
            return GdMath.Clampf(1.0 - (TotalPowerDraw() / PowerSupply), 0.0, 1.0);
        }

        public bool IsPowerOk() => IsPowerBudgetOk();

        public GdDict CanInstall(string componentId, double powerDraw)
        {
            var output = new GdDict { { "ok", false }, { "reason", "" } };
            if (string.IsNullOrEmpty(componentId))
            {
                output["reason"] = "no_component";
                return output;
            }
            if (TotalPowerDraw() + Math.Max(0.0, powerDraw) > PowerSupply)
            {
                output["reason"] = "power_budget";
                return output;
            }
            output["ok"] = true;
            return output;
        }

        /// <summary>Install from inventory Dictionary item_form->qty. Mutates inventory on success.</summary>
        public GdDict Install(
            string slotId,
            string componentId,
            string itemForm,
            GdDict inventory,
            double powerDraw = 5.0,
            double mass = 10.0,
            string sourceShip = "",
            bool plating = false)
        {
            var output = new GdDict { { "ok", false }, { "reason", "" }, { "slot_id", slotId } };
            if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(componentId) || string.IsNullOrEmpty(itemForm))
            {
                output["reason"] = "bad_args";
                return output;
            }
            foreach (object e in Installed)
            {
                if (e is GdDict entry && V.Str(entry.Get("slot_id", "")) == slotId)
                {
                    output["reason"] = "slot_occupied";
                    return output;
                }
            }
            if (V.I64(inventory.Get(itemForm, 0L)) < 1)
            {
                output["reason"] = "missing_item";
                return output;
            }
            GdDict gate = CanInstall(componentId, powerDraw);
            if (!gate.GetBool("ok", false))
            {
                output["reason"] = V.Str(gate.Get("reason", "blocked"));
                return output;
            }
            inventory[itemForm] = V.I64(inventory.Get(itemForm, 0L)) - 1;
            if (V.I64(inventory[itemForm]) <= 0)
                inventory.Erase(itemForm);
            Installed.Add(new GdDict
            {
                { "slot_id", slotId },
                { "component_id", componentId },
                { "item_form", itemForm },
                { "power_draw", Math.Max(0.0, powerDraw) },
                { "mass", Math.Max(0.0, mass) },
                { "source_ship", sourceShip },
                { "plating", plating },
            });
            if (plating)
                HullPlatingBonus += 0.05;
            output["ok"] = true;
            return output;
        }

        /// <summary>Uninstall slot back into inventory.</summary>
        public GdDict Uninstall(string slotId, GdDict inventory)
        {
            var output = new GdDict { { "ok", false }, { "reason", "" }, { "item_form", "" } };
            for (int i = 0; i < Installed.Count; i++)
            {
                if (!(Installed[i] is GdDict e))
                    continue;
                if (V.Str(e.Get("slot_id", "")) != slotId)
                    continue;
                string form = V.Str(e.Get("item_form", ""));
                if (!string.IsNullOrEmpty(form))
                    inventory[form] = V.I64(inventory.Get(form, 0L)) + 1;
                if (V.Bool(e.Get("plating", false)))
                    HullPlatingBonus = Math.Max(0.0, HullPlatingBonus - 0.05);
                Installed.RemoveAt(i);
                output["ok"] = true;
                output["item_form"] = form;
                return output;
            }
            output["reason"] = "not_found";
            return output;
        }

        public bool IsPowerBudgetOk() => TotalPowerDraw() <= PowerSupply + 0.001;

        /// <summary>REQ-SMOD-001: plating installs reduce hub structure damage (cap 50%).</summary>
        public double StructureDamageResist() => GdMath.Clampf(HullPlatingBonus * 2.0, 0.0, 0.5);

        public GdDict GetSummary() =>
            new GdDict
            {
                { "schema", "ship_modification_v1" },
                { "installed", Installed.DeepCopy() },
                { "power_supply", PowerSupply },
                { "power_demand_baseline", PowerDemandBaseline },
                { "power_draw", TotalPowerDraw() },
                { "power_ok", IsPowerBudgetOk() },
                { "min_operational_ratio", MinOperationalRatio },
                { "hull_plating_bonus", HullPlatingBonus },
            };

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            PowerSupply = Math.Max(0.0, V.F64(summary.Get("power_supply", PowerSupply)));
            PowerDemandBaseline = Math.Max(0.0, V.F64(summary.Get("power_demand_baseline", PowerDemandBaseline)));
            MinOperationalRatio = GdMath.Clampf(V.F64(summary.Get("min_operational_ratio", MinOperationalRatio)), 0.0, 1.0);
            HullPlatingBonus = Math.Max(0.0, V.F64(summary.Get("hull_plating_bonus", HullPlatingBonus)));
            object raw = summary.Get("installed", new GdArray());
            Installed.Clear();
            if (raw is GdArray rawArr)
            {
                foreach (object e in rawArr)
                    if (e is GdDict entry)
                        Installed.Add(entry.DeepCopy());
            }
            return true;
        }
    }
}
