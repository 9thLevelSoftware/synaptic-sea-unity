// Ported from scripts/tools/loot_container.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Searchable loot container. On first interaction it grants <c>loot_context.contents</c> when that key is present
    /// (authored stacks, including explicit empty), otherwise rolls its table deterministically (seed = container's
    /// seed_source) into the player InventoryState, then marks itself searched.
    /// </summary>
    public sealed class LootContainer : SessionInteractable
    {
        public override string Kind => "loot_container";

        /// <summary>signal container_searched(container_id, granted)</summary>
        public event Action<string, GdArray> ContainerSearched;

        public string ContainerId = "";
        public string LootTable = "";
        public string SeedSource = "";
        public InventoryState InventoryState;
        public GdDict Tables = new GdDict();
        public GdDict LootContext = new GdDict();

        /// <summary>
        /// <c>loot_context.get("unique_state")</c>: the UniqueItemState object the coordinator used to stash in the
        /// context dictionary (a GdDict cannot hold it).
        /// </summary>
        public UniqueItemState UniqueState;

        public bool Searched = false;
        public bool MarkerVisible = true;

        /// <summary>RUNTIME: <c>marker.visible = marker_visible and not searched</c>.</summary>
        public bool MarkerShown => MarkerVisible && !Searched;

        /// <summary>RUNTIME: <c>collision_shape.disabled = searched</c>.</summary>
        public bool CollisionDisabled => Searched;

        /// <summary>RUNTIME: GameplayPropFactory prop id for the visual ("corpse_bag" for corpse_* ids, else "loot_crate").</summary>
        public string PropId => GdString.BeginsWith(ContainerId, "corpse_") ? "corpse_bag" : "loot_crate";

        public void Configure(string containerId, string lootTable, string seedSource, InventoryState inventoryState, GdDict tables, Vec3 worldPosition, double radius = 1.8, GdDict lootContext = null, UniqueItemState uniqueState = null)
        {
            ContainerId = containerId;
            LootTable = lootTable;
            SeedSource = seedSource;
            InventoryState = inventoryState;
            Tables = tables;
            LootContext = (lootContext ?? new GdDict()).DeepCopy();
            UniqueState = uniqueState;
            InteractionRadius = radius;
            Searched = false;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "LootContainer_" + containerId;
            // RUNTIME: set_meta("loot_container", true), set_meta("container_id", ...); sphere collision (radius,
            // disabled = searched); GameplayPropFactory.build(PropId) visual, marker visible = MarkerShown.
        }

        public void SetSearched(bool value)
        {
            Searched = value;
            SetMarkerVisible(MarkerVisible);
            // RUNTIME: collision_shape.disabled = searched.
            NotifyChanged();
        }

        public void SetMarkerVisible(bool isVisible)
        {
            MarkerVisible = isVisible;
            // RUNTIME: marker.visible = marker_visible and not searched.
            NotifyChanged();
        }

        /// <summary>
        /// Explicit authored stacks (<c>contents</c> on the slice spec / loot_context). Accepts <c>qty</c> or
        /// <c>quantity</c>. Empty / invalid stacks are dropped.
        /// </summary>
        public static GdArray NormalizedContents(GdDict spec)
        {
            object raw = spec?.Get("contents", new GdArray());
            if (!(raw is GdArray rawArr))
                return new GdArray();
            var outArr = new GdArray();
            foreach (object stackV in rawArr)
            {
                if (!(stackV is GdDict stack))
                    continue;
                string itemId = V.Str(stack.Get("item_id", ""));
                long qty = V.I64(stack.Get("qty", stack.Get("quantity", 0L)));
                if (itemId.Length == 0 || qty <= 0)
                    continue;
                outArr.Add(new GdDict { { "item_id", itemId }, { "qty", qty }, { "quantity", qty } });
            }
            return outArr;
        }

        public bool TryInteract(Vec3 playerPosition)
        {
            if (Searched || InventoryState == null)
                return false;
            // Mirrors Interactable's validation bypass (derelict-placed sibling), not ToolPickup's stricter
            // always-check: a stale candidate_player may allow a one-time early search from out of range.
            if (!CandidatePlayerInRange && !IsPlayerInDirectRangeLenient(playerPosition))
                return false;
            GdArray granted;
            if (LootContext.Has("contents"))
                granted = GrantAuthoredContents();
            else
                granted = GrantRolledContents();
            // Searching consumes the container even if the bag was full (no re-roll on revisit).
            SetSearched(true);
            ContainerSearched?.Invoke(ContainerId, granted);
            return true;
        }

        GdArray GrantAuthoredContents()
        {
            var granted = new GdArray();
            object itemDefsV = LootContext.Has("item_definitions") ? LootContext["item_definitions"] : ItemDefs.LoadDefinitions();
            GdDict itemDefs = itemDefsV as GdDict ?? ItemDefs.LoadDefinitions();
            UniqueItemState uniqueState = UniqueState;
            foreach (object stackV in NormalizedContents(LootContext))
            {
                if (!(stackV is GdDict stack))
                    continue;
                string itemId = V.Str(stack.Get("item_id", ""));
                long qty = V.I64(stack.Get("quantity", stack.Get("qty", 0L)));
                if (itemId.Length == 0 || qty <= 0)
                    continue;
                string uniqueId = V.Str(stack.Get("unique_id", ItemDefs.UniqueId(itemDefs, itemId)));
                string seedKey = V.Str(stack.Get("seed_key", SeedSource + "|" + itemId));
                string codexEntryId = V.Str(stack.Get("codex_entry_id", ItemDefs.CodexEntryId(itemDefs, itemId)));
                if (uniqueState != null && uniqueId.Length != 0)
                {
                    if (!uniqueState.CanClaim(uniqueId, seedKey))
                        continue;
                }
                long added = InventoryState.AddItem(itemId, qty);
                if (added <= 0)
                    continue;
                var grantEntry = new GdDict
                {
                    { "item_id", itemId },
                    { "quantity", added },
                    { "seed_key", seedKey },
                };
                if (uniqueId.Length != 0)
                {
                    grantEntry["unique_id"] = uniqueId;
                    grantEntry["world_unique"] = true;
                }
                if (codexEntryId.Length != 0)
                    grantEntry["codex_entry_id"] = codexEntryId;
                granted.Add(grantEntry);
            }
            return granted;
        }

        GdArray GrantRolledContents()
        {
            var granted = new GdArray();
            GdArray rolled = UniqueState != null
                ? LootDistribution.RollWithUniqueState(LootTable, SeedSource, Tables, LootContext, UniqueState)
                : LootDistribution.Roll(LootTable, SeedSource, Tables, LootContext);
            foreach (object entryV in rolled)
            {
                var entry = (GdDict)entryV;
                string itemId = V.Str(entry.Get("item_id", ""));
                long qty = V.I64(entry.Get("quantity", 0L));
                if (itemId.Length == 0 || qty <= 0)
                    continue;
                long added = InventoryState.AddItem(itemId, qty);
                if (added > 0)
                {
                    GdDict grantEntry = entry.DeepCopy();
                    grantEntry["quantity"] = added;
                    granted.Add(grantEntry);
                }
            }
            return granted;
        }
    }
}
