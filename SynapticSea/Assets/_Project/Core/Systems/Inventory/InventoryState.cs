// Ported from scripts/systems/inventory_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Inventories that answer <c>can_accept(item_id, qty)</c> (the GDScript <c>has_method("can_accept")</c>
    /// probe in CraftingState). Only the player <see cref="InventoryState"/> implements it.
    /// </summary>
    public interface IItemAcceptor
    {
        bool CanAccept(string itemId, long qty);
    }

    /// <summary>
    /// Player-global inventory: quantitied, categorized (part/supply/tool), SOFT weight-capped
    /// (PZ-style: carrying over capacity is allowed and penalized via Heavy Load movement, NOT
    /// refused — see get_capacity/get_load_ratio/is_over_capacity; add_item gates on max_stack only).
    /// Pure model; never touches the scene tree. Tools are category 'tool' items, exposed
    /// through legacy shims (add_tool/has_tool/tool_ids/get_drain_multiplier) so OxygenState,
    /// ToolPickup, and the junction gate are untouched. Round-trips via get/apply_summary.
    /// </summary>
    public sealed partial class InventoryState : CargoTransfer.ICargoPlayer, CargoTransfer.ICargoHold, IItemAcceptor, IStatusLineProvider
    {
        public const string ITEM_DEFINITIONS_PATH = "res://data/items/item_definitions.json";
        public const string TOOL_DEFINITIONS_PATH = "res://data/tools/tool_definitions.json";
        public const double MAX_WEIGHT = 50.0;
        public const double DEFAULT_TOOL_WEIGHT = 2.0;
        public const long DEFAULT_MAX_STACK = 99;

        /// <summary>item_id: String -> quantity: int</summary>
        GdDict _trackedValueItems = new GdDict();
        public GdDict Items
        {
            get { if (_tracking == null) return _trackedValueItems; lock (CommonParticipantGate.SyncRoot) return _trackedValueItems; }
            set
            {
                if (_tracking == null) { _trackedValueItems = value; return; }
                lock (CommonParticipantGate.SyncRoot)
                {
                    _tracking.ReplaceRoot(_trackedValueItems, value); _trackedValueItems = value;
                }
            }
        }
        /// <summary>Actual player inventory debits, including cargo transfers; not synonymous with consumption.</summary>
        Action<string,long> _itemsRemoved;
        public event Action<string,long> ItemsRemoved
        {
            add { if (_tracking == null) { _itemsRemoved += value; return; } lock (CommonParticipantGate.SyncRoot) { _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _itemsRemoved += value; _tracking.TouchUnderGate(); } }
            remove { if (_tracking == null) { _itemsRemoved -= value; return; } lock (CommonParticipantGate.SyncRoot) { _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _itemsRemoved -= value; _tracking.TouchUnderGate(); } }
        }

        /// <summary>Added by worn containers (set by the coordinator).</summary>
        double _trackedValueBonusCapacity = 0.0;
        public double BonusCapacity
        {
            get { if (_tracking == null) return _trackedValueBonusCapacity; lock (CommonParticipantGate.SyncRoot) return _trackedValueBonusCapacity; }
            set
            {
                if (_tracking == null) { _trackedValueBonusCapacity = value; return; }
                lock (CommonParticipantGate.SyncRoot)
                {
                    _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _trackedValueBonusCapacity = value; _tracking.TouchUnderGate();
                }
            }
        }

        /// <summary>Saved kg from worn containers (set by the coordinator).</summary>
        double _trackedValueWeightReduction = 0.0;
        public double WeightReduction
        {
            get { if (_tracking == null) return _trackedValueWeightReduction; lock (CommonParticipantGate.SyncRoot) return _trackedValueWeightReduction; }
            set
            {
                if (_tracking == null) { _trackedValueWeightReduction = value; return; }
                lock (CommonParticipantGate.SyncRoot)
                {
                    _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _trackedValueWeightReduction = value; _tracking.TouchUnderGate();
                }
            }
        }
        /// <summary>Diagnostic owner projection; unique equipment is never represented in Items.</summary>
        Func<double> _trackedValueComponentMass = null;
        public Func<double> ComponentMass
        {
            get { if (_tracking == null) return _trackedValueComponentMass; lock (CommonParticipantGate.SyncRoot) return _trackedValueComponentMass; }
            set
            {
                if (_tracking == null) { _trackedValueComponentMass = value; return; }
                lock (CommonParticipantGate.SyncRoot)
                {
                    _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _trackedValueComponentMass = value; _tracking.TouchUnderGate();
                }
            }
        }
        Func<string, bool> _trackedValueRejectAnonymousComponent = null;
        public Func<string, bool> RejectAnonymousComponent
        {
            get { if (_tracking == null) return _trackedValueRejectAnonymousComponent; lock (CommonParticipantGate.SyncRoot) return _trackedValueRejectAnonymousComponent; }
            set
            {
                if (_tracking == null) { _trackedValueRejectAnonymousComponent = value; return; }
                lock (CommonParticipantGate.SyncRoot)
                {
                    _tracking.CheckCanAdvanceUnderGate(); _tracking.InvalidateProjectionUnderGate(); _trackedValueRejectAnonymousComponent = value; _tracking.TouchUnderGate();
                }
            }
        }

        GdDict _definitions = new GdDict(); // item_id -> def Dictionary (merged)

        public InventoryState()
        {
            LoadDefinitions();
        }

        // Admission-local proof reconstruction only. The owner supplies detached merged
        // definitions and keeps them private; this model never mutates definitions.
        internal InventoryState(GdDict normalizedDefinitions)
        {
            _definitions = normalizedDefinitions ?? throw new System.ArgumentNullException(nameof(normalizedDefinitions));
        }

        void LoadDefinitions()
        {
            SetDefinitions(ItemDefs.LoadDefinitions());
        }

        // --- definition helpers ---

        public GdDict GetDefinition(string itemId)
        {
            if (_tracking == null) { return GetDefinitionCore(itemId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetDefinitionCore(itemId);
            }
        }
        GdDict GetDefinitionCore(string itemId) => ItemDefs.GetDefinition(_definitions, itemId);

        public string GetCategory(string itemId)
        {
            if (_tracking == null) { return GetCategoryCore(itemId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetCategoryCore(itemId);
            }
        }
        string GetCategoryCore(string itemId) => ItemDefs.Category(_definitions, itemId);

        public double GetWeightEach(string itemId)
        {
            if (_tracking == null) { return GetWeightEachCore(itemId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetWeightEachCore(itemId);
            }
        }
        double GetWeightEachCore(string itemId) => ItemDefs.WeightEach(_definitions, itemId);

        long MaxStack(string itemId) => ItemDefs.MaxStack(_definitions, itemId);

        public string GetDisplayName(string itemId)
        {
            if (_tracking == null) { return GetDisplayNameCore(itemId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetDisplayNameCore(itemId);
            }
        }
        string GetDisplayNameCore(string itemId) => ItemDefs.DisplayName(_definitions, itemId);

        // --- item API ---

        public double GetMaxWeight() => MAX_WEIGHT;

        /// <summary>Effective carry budget = base cap + worn-container bonus (+ future strength).</summary>
        public double GetCapacity()
        {
            if (_tracking == null) { return GetCapacityCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetCapacityCore();
            }
        }
        double GetCapacityCore() => MAX_WEIGHT + BonusCapacity;

        /// <summary>
        /// Raw weight minus the worn-container weight reduction (saved kg), floored at 0.
        /// get_total_weight() stays the true mass; this is what encumbrance keys off.
        /// </summary>
        public double GetEffectiveWeight()
        {
            if (_tracking == null) { return GetEffectiveWeightCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetEffectiveWeightCore();
            }
        }
        double GetEffectiveWeightCore() => Math.Max(0.0, GetTotalWeight() - WeightReduction);

        /// <summary>effective_weight / capacity. &gt;1.0 means over-encumbered (Heavy Load).</summary>
        public double GetLoadRatio()
        {
            if (_tracking == null) { return GetLoadRatioCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetLoadRatioCore();
            }
        }
        double GetLoadRatioCore() => GetEffectiveWeight() / Math.Max(0.0001, GetCapacity());

        public bool IsOverCapacity()
        {
            if (_tracking == null) { return IsOverCapacityCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return IsOverCapacityCore();
            }
        }
        bool IsOverCapacityCore() => GetEffectiveWeight() > GetCapacity();

        public double GetTotalWeight()
        {
            if (_tracking == null) { return GetTotalWeightCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (ComponentMass != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                return GetTotalWeightCore();
            }
        }
        double GetTotalWeightCore() {
            double total = 0.0;
            foreach (var kv in Items)
                total += GetWeightEach(V.Str(kv.Key)) * V.F64(kv.Value);
            return total + (ComponentMass?.Invoke() ?? 0.0);
        }

        public long GetQuantity(string itemId)
        {
            if (_tracking == null) { return GetQuantityCore(itemId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetQuantityCore(itemId);
            }
        }
        long GetQuantityCore(string itemId) => V.I64(Items.Get(itemId, 0L));

        /// <summary>
        /// Adds up to qty, honoring max_stack ONLY. Weight does NOT gate (PZ soft-cap):
        /// the player may carry over capacity and suffer a Heavy Load movement penalty.
        /// Returns the quantity actually added (0 if the stack is full).
        /// </summary>
        public long AddItem(string itemId, long qty)
        {
            if (_tracking == null) { return AddItemCore(itemId, qty); }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (RejectAnonymousComponent != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                return AddItemCore(itemId, qty);
            }
        }
        long AddItemCore(string itemId, long qty) {
            if (RejectAnonymousComponent?.Invoke(itemId) == true) return 0;
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return 0;
            long current = GetQuantity(itemId);
            long stackRoom = Math.Max(0L, MaxStack(itemId) - current);
            long want = Math.Min(qty, stackRoom);
            if (want <= 0)
                return 0;
            Items[itemId] = current + want;
            return want;
        }

        /// <summary>
        /// Returns true if at least <paramref name="qty"/> of item_id can be added without exceeding max_stack.
        /// Weight is a soft-cap (never blocks); only the per-item stack ceiling gates here. Use to
        /// guard actions that consume inputs and then deposit an output (e.g. crafting), so the
        /// output is never silently dropped after the inputs are spent.
        /// </summary>
        public bool CanAccept(string itemId, long qty)
        {
            if (_tracking == null) { return CanAcceptCore(itemId, qty); }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (RejectAnonymousComponent != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                return CanAcceptCore(itemId, qty);
            }
        }
        bool CanAcceptCore(string itemId, long qty) {
            if (RejectAnonymousComponent?.Invoke(itemId) == true) return false;
            if (string.IsNullOrEmpty(itemId) || qty <= 0)
                return true;
            return (MaxStack(itemId) - GetQuantity(itemId)) >= qty;
        }

        public long RemoveItem(string itemId, long qty)
        {
            if (_tracking == null) return RemoveItemLegacy(itemId, qty);
            long removed; Action<string,long> notify;
            lock (CommonParticipantGate.SyncRoot)
            {
                if (RejectAnonymousComponent != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                if (qty <= 0) return 0;
                long current = GetQuantity(itemId); removed = Math.Min(qty, current);
                if (removed <= 0) return 0;
                if (removed >= current) Items.Erase(itemId); else Items[itemId] = current - removed;
                notify = _itemsRemoved;
            }
            notify?.Invoke(itemId, removed);
            return removed;
        }
        long RemoveItemLegacy(string itemId, long qty)
        {
            if (RejectAnonymousComponent?.Invoke(itemId) == true) return 0;
            if (qty <= 0)
                return 0;
            long current = GetQuantity(itemId);
            long removed = Math.Min(qty, current);
            if (removed <= 0)
                return 0;
            if (removed >= current)
                Items.Erase(itemId);
            else
                Items[itemId] = current - removed;
            _itemsRemoved?.Invoke(itemId,removed);
            return removed;
        }

        public GdArray GetItemsByCategory(string category)
        {
            if (_tracking == null) { return GetItemsByCategoryCore(category); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetItemsByCategoryCore(category);
            }
        }
        GdArray GetItemsByCategoryCore(string category) {
            var outArr = new GdArray();
            var ids = new List<object>(Items.Keys);
            GdSort.Sort(ids);
            foreach (object idV in ids)
            {
                string itemId = V.Str(idV);
                if (GetCategory(itemId) == category)
                {
                    outArr.Add(new GdDict
                    {
                        { "id", idV },
                        { "quantity", GetQuantity(itemId) },
                        { "weight_each", GetWeightEach(itemId) },
                    });
                }
            }
            return outArr;
        }

        public void Reset()
        {
            if (_tracking == null) { ResetLegacy(); return; }
            ulong stamp = CaptureTrackedStamp();
            if (!ResourceAuthorityPublication.TryAcquire(out var lease, out var reason)) throw new InvalidOperationException(reason);
            GdDict definitions = ItemDefs.LoadDefinitions(); // no resource/log callback inside gate
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                if (!lease.IsCurrent || !_tracking.MatchesUnderGate(stamp)) throw new InvalidOperationException("stale_resource_or_inventory");
                var prepared = _tracking.PrepareReplacements(new object[]{Items,_definitions},new object[]{new GdDict(),definitions},stamp);
                prepared.InstallUnderGate(attempt);
            }
        }
        void ResetLegacy()
        {
            Items.Clear();
            LoadDefinitions();
        }

        // --- legacy tool shims (REQ-007 consumers depend on these) ---

        /// <summary>GDScript computed property <c>tool_ids</c>: sorted ids whose category is 'tool'.</summary>
        public List<string> ToolIds { get { if (_tracking == null) return ToolIdsCore; lock (CommonParticipantGate.SyncRoot) return ToolIdsCore; } }
        List<string> ToolIdsCore
        {
            get
            {
                var outList = new List<string>();
                var ids = new List<object>(Items.Keys);
                GdSort.Sort(ids);
                foreach (object idV in ids)
                {
                    if (GetCategory(V.Str(idV)) == "tool")
                        outList.Add(V.Str(idV));
                }
                return outList;
            }
        }

        public bool AddTool(string toolId)
        {
            if (string.IsNullOrEmpty(toolId) || GetQuantity(toolId) > 0)
                return false;
            return AddItem(toolId, 1) == 1;
        }

        public bool HasTool(string toolId)
        {
            if (_tracking == null) { return HasToolCore(toolId); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return HasToolCore(toolId);
            }
        }
        bool HasToolCore(string toolId) => GetQuantity(toolId) > 0 && GetCategory(toolId) == "tool";

        public bool RemoveTool(string toolId) => RemoveItem(toolId, 1) == 1;

        public double GetDrainMultiplier()
        {
            if (_tracking == null) { return GetDrainMultiplierCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {

                return GetDrainMultiplierCore();
            }
        }
        double GetDrainMultiplierCore() => HasTool("portable_oxygen_pump") ? 0.5 : 1.0;

        // --- save/load ---

        public GdDict GetSummary()
        {
            if (_tracking == null) { return GetSummaryCore(); }
            lock (CommonParticipantGate.SyncRoot)
            {
                if (ComponentMass != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                return GetSummaryCore();
            }
        }
        GdDict GetSummaryCore() {
            var effects = new GdArray();
            List<string> toolIds = ToolIds;
            foreach (string toolId in toolIds)
            {
                object effect = GetDefinition(toolId).Get("effect", new GdDict());
                if (effect is GdDict effectDict)
                {
                    effects.Add(new GdDict
                    {
                        { "tool_id", toolId },
                        { "type", V.Str(effectDict.Get("type", "")) },
                        { "value", effectDict.Get("value", 1.0) },
                    });
                }
            }
            return new GdDict
            {
                { "items", Items.DeepCopy() },
                { "tool_ids", GdString.ToGdArray(ToolIds) },   // derived; kept for backward compat
                { "active_effects", effects },
                { "drain_multiplier", GetDrainMultiplier() },  // OxygenState consumes this
                { "total_weight", GetTotalWeight() },
                { "max_weight", GetMaxWeight() },
            };
        }

        /// <summary>Accepts the new ("items") shape AND the legacy ("tool_ids"-only) shape.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (_tracking == null) return ApplySummaryCore(summary);
            if (summary == null || summary.IsEmpty) return false;
            // Normalize source into private backing before touching live Items.
            var detached = new InventoryState(new GdDict());
            GdDict source;
            lock (CommonParticipantGate.SyncRoot) source = _tracking.ImportDict(summary).DeepCopy();
            detached.ApplySummary(source);
            using (var attempt = CommonParticipantGate.BeginAttempt())
            {
                var prepared = _tracking.PrepareReplacements(new object[]{Items},new object[]{detached.Items},_tracking.Stamp);
                prepared.InstallUnderGate(attempt);
            }
            return true;
        }
        bool ApplySummaryCore(GdDict summary) {
            if (summary == null || summary.IsEmpty)
                return false;
            Items.Clear();
            object itemsVariant = summary.Get("items", null);
            if (itemsVariant is GdDict itemsDict)
            {
                foreach (var kv in itemsDict)
                    Items[V.Str(kv.Key)] = V.I64(kv.Value);
            }
            else
            {
                // Legacy save: reconstruct tool items from tool_ids.
                object legacyIds = summary.Get("tool_ids", new GdArray());
                if (legacyIds is GdArray legacyArr)
                {
                    foreach (object toolId in legacyArr)
                        Items[V.Str(toolId)] = 1L;
                }
            }
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        { if (_tracking == null) return GetStatusLinesCore(); lock (CommonParticipantGate.SyncRoot) return GetStatusLinesCore(); }
        IReadOnlyList<string> GetStatusLinesCore()
        {
            var lines = new List<string>();
            // Tools first, preserving the REQ-007 markers the inventory HUD smoke greps.
            foreach (string toolId in ToolIds)
            {
                lines.Add("Tool: " + GetDisplayName(toolId));
                lines.Add("tool=" + toolId);
                if (toolId == "portable_oxygen_pump" && GetDrainMultiplier() != 1.0)
                    lines.Add("drain_multiplier=" + V.Str(GetDrainMultiplier()));
            }
            // Then non-tool items + a weight readout for the loot HUD.
            foreach (string cat in new[] { "part", "supply" })
            {
                foreach (object entryV in GetItemsByCategory(cat))
                {
                    var entry = (GdDict)entryV;
                    lines.Add("item=" + V.Str(entry["id"]) + " x" + GdString.FormatInt(V.I64(entry["quantity"])));
                }
            }
            lines.Add("weight=" + V.Str(GdMath.Snapped(GetTotalWeight(), 0.1)) + "/" + V.Str(GdMath.Snapped(GetCapacity(), 0.1)));
            return lines;
        }

        TrackedParticipantOwner _tracking;
        public static InventoryState CreateTracked(GdDict definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            var owner = new TrackedParticipantOwner();
            var model = new InventoryState(new GdDict());
            model._tracking = owner;
            model._trackedValueItems = owner.NewDict(true);
            model._definitions = owner.ImportDict(definitions, true);
            return model;
        }
        void SetDefinitions(GdDict definitions)
        {
            if (_tracking == null) { _definitions = definitions; return; }
            lock (CommonParticipantGate.SyncRoot)
            { var fresh = _tracking.ImportDict(definitions); _tracking.ReplaceRoot(_definitions, fresh); _definitions = fresh; }
        }
        internal GdDict CaptureTrackedSummary(out ulong stamp)
        {
            if (_tracking == null) throw new InvalidOperationException("untracked_inventory");
            lock (CommonParticipantGate.SyncRoot)
            {
                if (ComponentMass != null || RejectAnonymousComponent != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                GdDict result = GetSummaryCore(); stamp = _tracking.Stamp; return result;
            }
        }
        public GdDict ImportTrackedDictionary(GdDict source)
        { if (_tracking == null) throw new InvalidOperationException("untracked_participant"); return _tracking.ImportDict(source); }
        public GdArray ImportTrackedArray(GdArray source)
        { if (_tracking == null) throw new InvalidOperationException("untracked_participant"); return _tracking.ImportArray(source); }
        internal ulong CaptureTrackedStamp()
        { if (_tracking == null) throw new InvalidOperationException("untracked_inventory"); return _tracking.Stamp; }
        readonly object _maintainedReplacementIssuer = new object();
        internal PreparedReplacement PrepareTrackedReplacement(GdDict summary, ulong expectedStamp)
        {
            if (_tracking == null) throw new InvalidOperationException("untracked_inventory");
            lock (CommonParticipantGate.SyncRoot)
            {
                if (ComponentMass != null || RejectAnonymousComponent != null) throw new InvalidOperationException("unsupported_diagnostic_inventory_policy");
                if (summary == null || !(summary.Get("items") is GdDict items)) throw new ArgumentException("missing_inventory_items");
                foreach (var kv in items)
                    if (!(kv.Key is string) || !(kv.Value is long quantity) || quantity < 0) throw new ArgumentException("invalid_inventory_items");
                var debits = new List<KeyValuePair<string,long>>();
                foreach (var kv in Items)
                {
                    long debit = V.I64(kv.Value) - V.I64(items.Get(kv.Key, 0L));
                    if (debit > 0) debits.Add(new KeyValuePair<string,long>(V.Str(kv.Key), debit));
                }
                return new PreparedReplacement(this, _maintainedReplacementIssuer, _tracking.PrepareReplacements(new object[]{Items},new object[]{items},expectedStamp), _itemsRemoved, debits.ToArray());
            }
        }
        internal sealed class PreparedReplacement
        {
            readonly InventoryState _target;
            readonly PreparedVariantBacking _backing;
            readonly Action<string,long> _notification;
            readonly KeyValuePair<string,long>[] _debits;
            bool _installed, _notified;
            internal PreparedReplacement(InventoryState target, object issuer, PreparedVariantBacking backing,
                Action<string,long> notification, KeyValuePair<string,long>[] debits)
            {
                if (target == null || !ReferenceEquals(issuer, target._maintainedReplacementIssuer) || backing == null)
                    throw new ArgumentException("unissued_inventory_replacement");
                _target = target; _backing = backing; _notification = notification;
                _debits = (KeyValuePair<string,long>[])debits.Clone();
            }
            internal bool IsForModel(InventoryState target) => ReferenceEquals(_target, target);
            internal PreparedVariantBacking.ProjectionReplacementOrigin CaptureProjectionOrigin()
                => _backing.CaptureProjectionOrigin();
            internal bool MatchesUnderGate() => _backing.MatchesUnderGate();
            internal void InstallUnderGate(ParticipantPublicationAttempt attempt) { _backing.InstallUnderGate(attempt); _installed = true; }
            internal void RollbackUnderGate(ParticipantPublicationAttempt attempt) { _backing.RollbackUnderGate(attempt); _installed = false; }
            internal void NotifyAfterPublication()
            {
                if (System.Threading.Monitor.IsEntered(CommonParticipantGate.SyncRoot) || !_installed || _notified) throw new InvalidOperationException("invalid_inventory_notification");
                _notified = true;
                foreach (var debit in _debits) _notification?.Invoke(debit.Key, debit.Value);
            }
        }
    }
}
