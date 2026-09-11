// Ported from scripts/systems/station_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for a crafting station. Tracks station kind, level/tier, power state,
    /// active recipe, batch queue, and progress. Never touches the scene tree.
    /// PKG-B2.4b: tier from installed components; max_queue + batch enqueue.
    /// </summary>
    public sealed class StationState : ISimModel, IStatusLineProvider
    {
        public enum Status
        {
            IDLE = 0,
            CRAFTING = 1,
            PAUSED_POWER = 2,
            COMPLETE = 3,
            PAUSED_NO_MATERIALS = 4,
        }

        public const long DEFAULT_MAX_QUEUE = 8;

        public string StationKind = "";     // e.g. "fabricator", "workbench", "kitchen"
        public long Level = 0;              // upgrade level (0 = base); mirrors tier when unset
        public long Tier = 0;               // PKG-B2.4b: effective station tier (component-derived)
        public bool Powered = true;         // power available
        public string ActiveRecipeId = "";  // currently crafting recipe
        public double ProgressSeconds = 0.0; // elapsed craft time
        public double RequiredSeconds = 0.0; // total craft time for active recipe
        /// <summary>GDScript <c>status</c> (an int holding a <see cref="Status"/> value).</summary>
        public long CurrentStatus = (long)Status.IDLE;
        public List<string> Queue = new List<string>(); // queued recipe_ids
        public long MaxQueue = DEFAULT_MAX_QUEUE;

        public void Configure(GdDict config)
        {
            config = config ?? new GdDict();
            StationKind = V.Str(config.Get("station_kind", ""));
            Level = Math.Max(0L, V.I64(config.Get("level", 0L)));
            Tier = Math.Max(0L, V.I64(config.Get("tier", Level)));
            Powered = V.Bool(config.Get("powered", true));
            MaxQueue = Math.Max(1L, V.I64(config.Get("max_queue", DEFAULT_MAX_QUEUE)));
            ActiveRecipeId = "";
            ProgressSeconds = 0.0;
            RequiredSeconds = 0.0;
            CurrentStatus = (long)Status.IDLE;
            Queue.Clear();
            if (config.Get("queue", new GdArray()) is GdArray q)
            {
                foreach (object item in q)
                {
                    string rid = V.Str(item);
                    if (rid.Length != 0 && Queue.Count < MaxQueue) Queue.Add(rid);
                }
            }
        }

        /// <summary>PKG-B2.4b: set tier from installed components (max of bonuses, at least level).</summary>
        public void ApplyComponentTier(long componentTierBonus)
        {
            Tier = Math.Max(Level, Math.Max(0L, componentTierBonus));
        }

        public long EffectiveTier() => Math.Max(Tier, Level);

        /// <summary>Start crafting a recipe. Returns true if started.</summary>
        public bool StartRecipe(string recipeId, double craftTime)
        {
            if (string.IsNullOrEmpty(recipeId) || craftTime <= 0.0) return false;
            if (!Powered)
            {
                CurrentStatus = (long)Status.PAUSED_POWER;
                ActiveRecipeId = recipeId;
                RequiredSeconds = craftTime;
                ProgressSeconds = 0.0;
                return false;
            }
            ActiveRecipeId = recipeId;
            RequiredSeconds = craftTime;
            ProgressSeconds = 0.0;
            CurrentStatus = (long)Status.CRAFTING;
            return true;
        }

        /// <summary>Queue a recipe for later. Returns false if queue is full or id empty.</summary>
        public bool Enqueue(string recipeId)
        {
            if (string.IsNullOrEmpty(recipeId)) return false;
            if (Queue.Count >= MaxQueue) return false;
            Queue.Add(recipeId);
            return true;
        }

        /// <summary>PKG-B2.4b: enqueue the same recipe count times (batch). Returns accepted count.</summary>
        public long EnqueueBatch(string recipeId, long count)
        {
            if (string.IsNullOrEmpty(recipeId) || count <= 0) return 0;
            long accepted = 0;
            for (long i = 0; i < count; i++)
            {
                if (!Enqueue(recipeId)) break;
                accepted += 1;
            }
            return accepted;
        }

        public long QueueSpace() => Math.Max(0L, MaxQueue - Queue.Count);

        public string Dequeue()
        {
            if (Queue.Count == 0) return "";
            string first = Queue[0];
            Queue.RemoveAt(0);
            return first;
        }

        /// <summary>Advance craft progress by delta_seconds. Returns true when the craft completes.</summary>
        public bool Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return false;
            if (CurrentStatus == (long)Status.COMPLETE) return false;
            if (!Powered)
            {
                if (CurrentStatus == (long)Status.CRAFTING) CurrentStatus = (long)Status.PAUSED_POWER;
                return false;
            }
            if (CurrentStatus == (long)Status.PAUSED_POWER && Powered) CurrentStatus = (long)Status.CRAFTING;
            if (CurrentStatus != (long)Status.CRAFTING) return false;
            ProgressSeconds += deltaSeconds;
            if (ProgressSeconds >= RequiredSeconds)
            {
                ProgressSeconds = RequiredSeconds;
                CurrentStatus = (long)Status.COMPLETE;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Mark the completed craft as consumed and advance to the next queued recipe.
        /// Returns the next recipe_id if one was queued, else empty.
        /// </summary>
        public string FinishAndAdvance()
        {
            if (CurrentStatus != (long)Status.COMPLETE) return "";
            double previousRequiredSeconds = RequiredSeconds;
            ActiveRecipeId = "";
            ProgressSeconds = 0.0;
            RequiredSeconds = 0.0;
            CurrentStatus = (long)Status.IDLE;
            string next = Dequeue();
            if (next.Length != 0)
            {
                ActiveRecipeId = next;
                RequiredSeconds = previousRequiredSeconds;
                ProgressSeconds = 0.0;
                CurrentStatus = Powered ? (long)Status.CRAFTING : (long)Status.PAUSED_POWER;
            }
            return next;
        }

        public void SetPower(bool p)
        {
            Powered = p;
            if (!p && CurrentStatus == (long)Status.CRAFTING) CurrentStatus = (long)Status.PAUSED_POWER;
            else if (p && CurrentStatus == (long)Status.PAUSED_POWER) CurrentStatus = (long)Status.CRAFTING;
        }

        public bool IsCrafting() => CurrentStatus == (long)Status.CRAFTING || CurrentStatus == (long)Status.PAUSED_POWER;

        public double GetProgressRatio()
        {
            if (RequiredSeconds <= 0.0) return 0.0;
            return GdMath.Clampf(ProgressSeconds / RequiredSeconds, 0.0, 1.0);
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "station_kind", StationKind },
                { "level", Level },
                { "tier", Tier },
                { "max_queue", MaxQueue },
                { "powered", Powered },
                { "active_recipe_id", ActiveRecipeId },
                { "progress_seconds", ProgressSeconds },
                { "required_seconds", RequiredSeconds },
                { "status", CurrentStatus },
                { "queue", ItemsCompat.ToGdArray(Queue) },
            };
        }

        /// <summary>Returns true when any field changed, like the GDScript.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            string newKind = V.Str(summary.Get("station_kind", StationKind));
            if (newKind != StationKind) { StationKind = newKind; changed = true; }
            long newLevel = V.I64(summary.Get("level", Level));
            if (newLevel != Level) { Level = newLevel; changed = true; }
            long newTier = V.I64(summary.Get("tier", Tier));
            if (newTier != Tier) { Tier = newTier; changed = true; }
            long newMaxQ = V.I64(summary.Get("max_queue", MaxQueue));
            if (newMaxQ != MaxQueue && newMaxQ >= 1) { MaxQueue = newMaxQ; changed = true; }
            bool newPowered = V.Bool(summary.Get("powered", Powered));
            if (newPowered != Powered) { Powered = newPowered; changed = true; }
            string newRecipe = V.Str(summary.Get("active_recipe_id", ActiveRecipeId));
            if (newRecipe != ActiveRecipeId) { ActiveRecipeId = newRecipe; changed = true; }
            double newProg = V.F64(summary.Get("progress_seconds", ProgressSeconds));
            if (Math.Abs(newProg - ProgressSeconds) > 0.001) { ProgressSeconds = newProg; changed = true; }
            double newReq = V.F64(summary.Get("required_seconds", RequiredSeconds));
            if (Math.Abs(newReq - RequiredSeconds) > 0.001) { RequiredSeconds = newReq; changed = true; }
            long newStatus = V.I64(summary.Get("status", CurrentStatus));
            if (newStatus != CurrentStatus) { CurrentStatus = newStatus; changed = true; }
            if (summary.Get("queue", new GdArray()) is GdArray arr)
            {
                if (!QueueEquals(arr))
                {
                    Queue.Clear();
                    foreach (object item in arr) Queue.Add(V.Str(item));
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>GDScript <c>arr != queue</c> (untyped Array vs Array[String]): same size and type-strict element equality.</summary>
        bool QueueEquals(GdArray arr)
        {
            if (arr.Count != Queue.Count) return false;
            for (int i = 0; i < arr.Count; i++)
                if (!(arr[i] is string s) || !string.Equals(s, Queue[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            string statusName = "IDLE";
            switch (CurrentStatus)
            {
                case (long)Status.CRAFTING: statusName = "CRAFTING"; break;
                case (long)Status.PAUSED_POWER: statusName = "PAUSED_POWER"; break;
                case (long)Status.PAUSED_NO_MATERIALS: statusName = "PAUSED_NO_MATERIALS"; break;
                case (long)Status.COMPLETE: statusName = "COMPLETE"; break;
            }
            lines.Add("Station: " + StationKind + " L" + ItemsCompat.D(Level) + " T" + ItemsCompat.D(EffectiveTier()) + " [" + statusName + "]");
            if (IsCrafting() || CurrentStatus == (long)Status.COMPLETE)
                lines.Add("Recipe: " + ActiveRecipeId + " " + ItemsCompat.Fmt(ProgressSeconds, 1) + "/" + ItemsCompat.Fmt(RequiredSeconds, 1) + "s");
            if (Queue.Count != 0)
                lines.Add("Queue: " + ItemsCompat.D(Queue.Count) + "/" + ItemsCompat.D(MaxQueue));
            return lines;
        }
    }
}
