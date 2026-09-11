// Ported from scripts/systems/water_recycler_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for the water recycler / purifier.
    /// Converts contaminated water into purified water over time.
    /// Never touches the scene tree.
    /// </summary>
    public sealed class WaterRecyclerState : ISimModel, IStatusLineProvider
    {
        public enum State { IDLE = 0, RECYCLING = 1 }

        public string InputItemId = "";
        public string OutputItemId = "purified_water";
        public double ConversionRatio = 1.0; // 1 input -> 1 output
        public double RecycleTimeSeconds = 30.0;
        public double PowerCost = 5.0;

        /// <summary>GDScript <c>state</c> (an int holding a <see cref="State"/> value).</summary>
        public long CurrentState = (long)State.IDLE;
        public double ProgressSeconds = 0.0;
        public long InputQuantity = 0;
        public long OutputReady = 0;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            InputItemId = V.Str(config.Get("input_item_id", ""));
            OutputItemId = V.Str(config.Get("output_item_id", "purified_water"));
            ConversionRatio = V.F64(config.Get("conversion_ratio", 1.0));
            RecycleTimeSeconds = Math.Max(0.1, V.F64(config.Get("recycle_time_seconds", 30.0)));
            PowerCost = V.F64(config.Get("power_cost", 5.0));
            CurrentState = (long)State.IDLE;
            ProgressSeconds = 0.0;
            InputQuantity = 0;
            OutputReady = 0;
        }

        public GdDict LoadInput(string itemId, long qty, double availablePower)
        {
            if (CurrentState != (long)State.IDLE) return new GdDict { { "ok", false }, { "reason", "not_idle" } };
            if (availablePower < PowerCost) return new GdDict { { "ok", false }, { "reason", "insufficient_power" } };
            if (qty <= 0) return new GdDict { { "ok", false }, { "reason", "no_input" } };
            InputItemId = itemId;
            InputQuantity = qty;
            CurrentState = (long)State.RECYCLING;
            ProgressSeconds = 0.0;
            return new GdDict { { "ok", true }, { "reason", "" } };
        }

        public bool Tick(double deltaSeconds)
        {
            if (CurrentState != (long)State.RECYCLING) return false;
            if (deltaSeconds <= 0.0) return false;
            ProgressSeconds += deltaSeconds;
            if (ProgressSeconds >= RecycleTimeSeconds)
            {
                CurrentState = (long)State.IDLE;
                OutputReady = GdMath.Trunc((double)InputQuantity * ConversionRatio);
                InputQuantity = 0;
                return true;
            }
            return false;
        }

        public double GetProgressRatio()
        {
            if (RecycleTimeSeconds <= 0.0) return 0.0;
            return GdMath.Clampf(ProgressSeconds / RecycleTimeSeconds, 0.0, 1.0);
        }

        public GdDict CollectOutput()
        {
            if (OutputReady <= 0) return new GdDict { { "ok", false }, { "item_id", "" }, { "quantity", 0L } };
            var outDict = new GdDict
            {
                { "ok", true },
                { "item_id", OutputItemId },
                { "quantity", OutputReady },
            };
            OutputReady = 0;
            return outDict;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "input_item_id", InputItemId },
                { "output_item_id", OutputItemId },
                { "conversion_ratio", ConversionRatio },
                { "recycle_time_seconds", RecycleTimeSeconds },
                { "power_cost", PowerCost },
                { "state", CurrentState },
                { "progress_seconds", ProgressSeconds },
                { "input_quantity", InputQuantity },
                { "output_ready", OutputReady },
                { "progress_ratio", GetProgressRatio() },
            };
        }

        /// <summary>
        /// Returns true when any field changed. The GDScript loops over key lists with <c>get(key)</c>/<c>set(key)</c>;
        /// the same keys are applied here in the same order.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            // String keys.
            string s = V.Str(summary.Get("input_item_id", InputItemId));
            if (s != InputItemId) { InputItemId = s; changed = true; }
            s = V.Str(summary.Get("output_item_id", OutputItemId));
            if (s != OutputItemId) { OutputItemId = s; changed = true; }
            // Float keys.
            double f = V.F64(summary.Get("conversion_ratio", ConversionRatio));
            if (Math.Abs(f - ConversionRatio) > 0.001) { ConversionRatio = f; changed = true; }
            f = V.F64(summary.Get("recycle_time_seconds", RecycleTimeSeconds));
            if (Math.Abs(f - RecycleTimeSeconds) > 0.001) { RecycleTimeSeconds = f; changed = true; }
            f = V.F64(summary.Get("power_cost", PowerCost));
            if (Math.Abs(f - PowerCost) > 0.001) { PowerCost = f; changed = true; }
            f = V.F64(summary.Get("progress_seconds", ProgressSeconds));
            if (Math.Abs(f - ProgressSeconds) > 0.001) { ProgressSeconds = f; changed = true; }
            // Int keys.
            long i = V.I64(summary.Get("state", CurrentState));
            if (i != CurrentState) { CurrentState = i; changed = true; }
            i = V.I64(summary.Get("input_quantity", InputQuantity));
            if (i != InputQuantity) { InputQuantity = i; changed = true; }
            i = V.I64(summary.Get("output_ready", OutputReady));
            if (i != OutputReady) { OutputReady = i; changed = true; }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string stateName = "IDLE";
            if (CurrentState == (long)State.RECYCLING) stateName = "RECYCLING";
            lines.Add("Water Recycler: " + OutputItemId + " [" + stateName + "]");
            if (CurrentState == (long)State.RECYCLING)
                lines.Add("  progress=" + ItemsCompat.D((long)GdMath.Round(GetProgressRatio() * 100.0)) + "% input=" + ItemsCompat.D(InputQuantity));
            if (OutputReady > 0)
                lines.Add("  ready: " + OutputItemId + " x" + ItemsCompat.D(OutputReady));
            return lines;
        }
    }
}
