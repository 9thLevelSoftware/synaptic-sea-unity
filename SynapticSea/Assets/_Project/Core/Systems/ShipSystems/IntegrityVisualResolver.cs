// Ported from scripts/systems/integrity_visual_resolver.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// RUNTIME: a ship scene root seen as its structural modules. Godot walked the <c>StructuralModules</c> child
    /// (falling back to the ship root itself) and visited every Node3D child in child order; the Runtime layer
    /// returns those wrappers as <see cref="IModuleSceneView"/>s in the same order.
    /// </summary>
    public interface IShipModuleScene
    {
        IEnumerable<IModuleSceneView> StructuralModuleViews();
    }

    /// <summary>
    /// Maps integrity state to the correct visual child in a structural wrapper scene. Does not change module
    /// identity, collision, or navigation — only visibility. Scene effects go through <see cref="IModuleSceneView"/>.
    /// </summary>
    public static class IntegrityVisualResolver
    {
        public const string STATE_INTACT = "intact";
        public const string STATE_DAMAGED = "damaged";
        public const string STATE_BREACHED = "breached";
        public const string STATE_DESTROYED = "destroyed";

        public const string VISUAL_INTACT = "VisualInstance_Intact";
        public const string VISUAL_DAMAGED = "VisualInstance_Damaged";
        public const string VISUAL_BREACHED = "VisualInstance_Breached";
        public const string VISUAL_LEGACY = "VisualInstance";

        /// <summary>
        /// Apply visual state to a wrapper node's Visual child group.
        /// Returns true if a matching visual was found and toggled.
        /// </summary>
        public static bool ApplyVisualState(IModuleSceneView wrapperNode, string state)
        {
            if (wrapperNode == null)
                return false;
            if (!wrapperNode.HasVisualGroup)
                return false;

            bool intact = wrapperNode.HasVisual(VISUAL_INTACT);
            bool damaged = wrapperNode.HasVisual(VISUAL_DAMAGED);
            bool breached = wrapperNode.HasVisual(VISUAL_BREACHED);

            // Legacy single-child fallback.
            if (!intact && !damaged && !breached)
            {
                if (!wrapperNode.HasVisual(VISUAL_LEGACY))
                    return false;
                GdDict consequence = ModuleIntegrityConsequences.ConsequenceForState(state);
                object tintV = consequence.Get("modulate", GdArray.Of(1.0, 1.0, 1.0, 1.0));
                if (tintV is GdArray tint && tint.Count >= 4)
                {
                    // RUNTIME: _apply_legacy_tint set a StandardMaterial3D albedo override on every
                    // GeometryInstance3D under Visual/VisualInstance (alpha transparency below 0.99). Color is float32.
                    wrapperNode.TintMeshes(
                        VISUAL_LEGACY,
                        (float)V.F64(tint[0]),
                        (float)V.F64(tint[1]),
                        (float)V.F64(tint[2]),
                        (float)V.F64(tint[3]));
                }
                wrapperNode.SetVisualVisible(VISUAL_LEGACY, state != STATE_DESTROYED);
                return true;
            }

            // Variant-aware wrapper: hide all, then show the requested variant.
            if (intact)
                wrapperNode.SetVisualVisible(VISUAL_INTACT, false);
            if (damaged)
                wrapperNode.SetVisualVisible(VISUAL_DAMAGED, false);
            if (breached)
                wrapperNode.SetVisualVisible(VISUAL_BREACHED, false);

            switch (state)
            {
                case STATE_INTACT:
                    if (intact)
                    {
                        wrapperNode.SetVisualVisible(VISUAL_INTACT, true);
                        return true;
                    }
                    break;
                case STATE_DAMAGED:
                    if (damaged)
                    {
                        wrapperNode.SetVisualVisible(VISUAL_DAMAGED, true);
                        return true;
                    }
                    break;
                case STATE_BREACHED:
                    if (breached)
                    {
                        wrapperNode.SetVisualVisible(VISUAL_BREACHED, true);
                        return true;
                    }
                    break;
                case STATE_DESTROYED:
                    // All variants hidden means the wrapper has no visible mesh.
                    return true;
            }
            return false;
        }

        /// <summary>Batch-apply integrity visuals to all modules in a ship scene tree.</summary>
        public static long ApplyToShip(IShipModuleScene shipRoot, IModuleIntegrityMap moduleMap)
        {
            if (shipRoot == null || moduleMap == null)
                return 0;
            long applied = 0;
            // RUNTIME: Godot iterated StructuralModules (or ship_root) children that are Node3D; child.name is the id.
            foreach (IModuleSceneView child in shipRoot.StructuralModuleViews())
            {
                if (child == null)
                    continue;
                string moduleId = child.ModuleKey;
                string state = moduleMap.GetState(moduleId);
                if (ApplyVisualState(child, state))
                    applied += 1;
            }
            return applied;
        }
    }
}
