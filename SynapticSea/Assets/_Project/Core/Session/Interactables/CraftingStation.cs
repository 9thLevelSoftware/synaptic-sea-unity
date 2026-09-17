// Ported from scripts/tools/crafting_station.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A spatial, range-gated crafting/salvage station bound to a station_kind on the home ship. Interaction hands
    /// work to the coordinator-owned models: a normal station requests the recipe picker (REQ-CS-016) and
    /// <see cref="TryCraftRecipe"/> begins the chosen recipe via CraftingState; a "salvage" station opens the same
    /// picker with deconstruct + junk targets (REQ-CS-017) and <see cref="TrySalvageTarget"/> runs
    /// DeconstructionResolver. Never advances crafting itself.
    /// </summary>
    public sealed class CraftingStation : SessionInteractable
    {
        public override string Kind => "crafting_station";

        /// <summary>
        /// Optional coordinator hook for medbay field surgery (Stream F): Godot's duck-typed
        /// <c>surgery_provider.try_medbay_surgery(player_body)</c>.
        /// </summary>
        public interface ISurgeryProvider
        {
            bool TryMedbaySurgery();
        }

        /// <summary>signal craft_started(station_kind, recipe_id)</summary>
        public event Action<string, string> CraftStarted;
        /// <summary>signal salvage_completed(item_id, yields)</summary>
        public event Action<string, GdDict> SalvageCompleted;
        /// <summary>signal craft_blocked(station_kind, reason)</summary>
        public event Action<string, string> CraftBlocked;
        /// <summary>signal recipe_picker_requested(station_kind) — REQ-CS-016.</summary>
        public event Action<string> RecipePickerRequested;

        public string StationKind = "";
        public CraftingState CraftingState;
        public MaterialState MaterialState;
        public InventoryState InventoryState;
        public DeconstructionResolver DeconstructionResolver;
        public PlayerProgressionState PlayerProgression;
        /// <summary>Optional coordinator ref for medbay surgery (Stream F).</summary>
        public ISurgeryProvider SurgeryProvider = null;
        /// <summary>Mirrors the model station; gates feedback only.</summary>
        public bool Powered = true;
        public bool MarkerVisible = true;

        /// <summary>RUNTIME: <c>marker.visible = marker_visible</c>.</summary>
        public bool MarkerShown => MarkerVisible;

        public void Configure(string stationKind, CraftingState craftingState, MaterialState materialState, InventoryState inventoryState, DeconstructionResolver deconstructionResolver, PlayerProgressionState playerProgression, Vec3 worldPosition, double radius = 1.8)
        {
            // Debug-build guards (player_progression is intentionally optional).
            Debug.Assert(craftingState != null, "p_crafting_state must not be null");
            Debug.Assert(materialState != null, "p_material_state must not be null");
            Debug.Assert(inventoryState != null, "p_inventory_state must not be null");
            Debug.Assert(deconstructionResolver != null, "p_deconstruction_resolver must not be null");
            Debug.Assert(radius >= 0.0, "radius must be non-negative");
            StationKind = stationKind;
            CraftingState = craftingState;
            MaterialState = materialState;
            InventoryState = inventoryState;
            DeconstructionResolver = deconstructionResolver;
            PlayerProgression = playerProgression;
            InteractionRadius = radius;
            CandidatePlayerInRange = false;
            LocalPosition = worldPosition;
            NodeName = "CraftingStation_" + stationKind;
            // RUNTIME: set_meta("crafting_station", true), set_meta("station_kind", ...); sphere collision (radius);
            // GameplayPropFactory.build("workbench") visual, marker visible = MarkerShown.
        }

        public void SetPowered(bool value)
        {
            Powered = value;
        }

        public void SetMarkerVisible(bool isVisible)
        {
            MarkerVisible = isVisible;
            // RUNTIME: marker.visible = marker_visible.
            NotifyChanged();
        }

        long PlayerSkill()
        {
            if (PlayerProgression != null)
                return PlayerProgression.GetSkillLevel("fabrication");
            return 0;
        }

        /// <summary>
        /// Range-gated interact. Returns true if it opened the recipe picker, blocked as busy, or ran medbay surgery.
        /// </summary>
        public bool TryInteract(Vec3 playerPosition)
        {
            if (CraftingState == null || InventoryState == null)
                return false;
            if (!IsPlayerInDirectRangeStrict(playerPosition))
                return false;
            // Single active craft: if one is already running, block with feedback and consume interact.
            if (CraftingState.IsCrafting())
            {
                CraftBlocked?.Invoke(StationKind, "busy");
                return true;
            }
            // Stream F: medbay field surgery when the patient is critical (before crafts).
            if (StationKind == "medbay" && SurgeryProvider != null)
            {
                if (SurgeryProvider.TryMedbaySurgery())
                    return true;
            }
            // REQ-CS-016 / REQ-CS-017: open the recipe/salvage picker (no auto-select).
            RecipePickerRequested?.Invoke(StationKind);
            return true;
        }

        /// <summary>Explicit craft for a chosen recipe_id (picker confirm + validation seams).</summary>
        public bool TryCraftRecipe(string recipeId)
        {
            if (string.IsNullOrEmpty(recipeId) || CraftingState == null || InventoryState == null)
            {
                CraftBlocked?.Invoke(StationKind, "no_craftable_recipe");
                return false;
            }
            if (CraftingState.IsCrafting())
            {
                CraftBlocked?.Invoke(StationKind, "busy");
                return false;
            }
            if (CraftingState.GetStationKind(recipeId) != StationKind)
            {
                CraftBlocked?.Invoke(StationKind, "wrong_station");
                return false;
            }
            if (V.Str(CraftingState.GetRecipe(recipeId).Get("category", "")) == "deconstruction")
            {
                CraftBlocked?.Invoke(StationKind, "deconstruction_not_here");
                return false;
            }
            if (!CraftingState.CanCraft(recipeId, InventoryState))
            {
                CraftBlocked?.Invoke(StationKind, "missing_ingredients");
                return false;
            }
            if (CraftingState.GetRequiredSkillLevel(recipeId) > PlayerSkill())
            {
                CraftBlocked?.Invoke(StationKind, "insufficient_skill");
                return false;
            }
            GdDict produces = CraftingState.GetProduces(recipeId);
            if (!InventoryState.CanAccept(V.Str(produces.Get("item_id", "")), V.I64(produces.Get("quantity", 0L))))
            {
                CraftBlocked?.Invoke(StationKind, "output_full");
                return false;
            }
            if (CraftingState.BeginCraft(recipeId, InventoryState, MaterialState, PlayerSkill()))
            {
                CraftStarted?.Invoke(StationKind, recipeId);
                return true;
            }
            CraftBlocked?.Invoke(StationKind, "begin_failed");
            return false;
        }

        /// <summary>First ready recipe for this station (validation / auto-smoke path). Empty if none.</summary>
        public string FirstReadyRecipeId()
        {
            if (StationKind == "salvage")
                return FirstReadySalvageId();
            if (CraftingState == null || InventoryState == null)
                return "";
            GdArray entries = CraftingState.ListRecipeEntries(StationKind, InventoryState, PlayerSkill());
            foreach (object entry in entries)
            {
                if (entry is GdDict d && V.Bool(d.Get("craftable", false)))
                    return V.Str(d.Get("recipe_id", ""));
            }
            return "";
        }

        public string FirstReadySalvageId()
        {
            if (DeconstructionResolver == null || InventoryState == null)
                return "";
            return DeconstructionResolver.FirstReadySalvageId(InventoryState);
        }

        /// <summary>REQ-CS-017: execute a chosen salvage target (deconstruct recipe_id or junk:&lt;item&gt;).</summary>
        public bool TrySalvageTarget(string targetId)
        {
            if (StationKind != "salvage")
            {
                CraftBlocked?.Invoke(StationKind, "not_salvage");
                return false;
            }
            if (string.IsNullOrEmpty(targetId) || DeconstructionResolver == null || MaterialState == null)
            {
                CraftBlocked?.Invoke(StationKind, "no_resolver");
                return false;
            }
            GdDict produced = DeconstructionResolver.ExecuteSalvageTarget(targetId, InventoryState, MaterialState);
            if (produced == null || produced.IsEmpty)
            {
                CraftBlocked?.Invoke(StationKind, "nothing_to_salvage");
                return false;
            }
            string outId = V.Str(produced.Get("item_id", ""));
            long outQty = V.I64(produced.Get("quantity", 0L));
            // Deconstruct returns produces without depositing; junk already deposited materials.
            if (!GdString.BeginsWith(targetId, "junk:"))
            {
                if (outId.Length != 0 && outQty > 0)
                    InventoryState.AddItem(outId, outQty);
            }
            SalvageCompleted?.Invoke(outId, produced);
            return true;
        }
    }
}
