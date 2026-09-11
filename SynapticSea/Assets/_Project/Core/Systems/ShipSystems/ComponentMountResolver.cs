// Ported from scripts/systems/component_mount_resolver.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.3b: resolve completed mount/dismount WorkActions against ComponentPlacementState.
    /// Pure — mutates placement + inventory Dictionary; scene applies encumbrance/UI.
    /// </summary>
    /// <remarks>
    /// <c>work</c> is a <see cref="WorkActionState"/> (it always has <c>noise()</c> / <c>xp_event()</c>, so the
    /// GDScript <c>has_method</c> fallbacks 0.15/0.20 and "salvage"/"repair" are unreachable). <c>placement</c> is
    /// duck-typed in GDScript (<c>placement.call("dismount"/"mount", ...)</c>); ComponentPlacementState is not ported
    /// yet, so its surface is the <see cref="IComponentPlacement"/> interface below, which that port must implement.
    /// </remarks>
    public static class ComponentMountResolver
    {
        /// <summary>ComponentPlacementState surface used by the resolver.</summary>
        public interface IComponentPlacement
        {
            /// <summary><c>dismount(instance_id) -> {ok, reason, item_form, mass, qty, component_id, instance_id, linked_system, linked_subcomponent}</c>.</summary>
            GdDict Dismount(string instanceId);

            /// <summary><c>mount(item_form, room_id, slot_kind, slot_index, inventory, catalog) -> {ok, reason, instance_id, item_form}</c>; mutates inventory.</summary>
            GdDict Mount(string itemForm, string roomId, string slotKind, long slotIndex, GdDict inventory, ComponentCatalog catalog = null);
        }

        /// <summary>Complete a dismount WorkAction. work.target_id must be component_instance_id.</summary>
        public static GdDict ResolveDismount(
            WorkActionState work,
            IComponentPlacement placement,
            GdDict inventory)
        {
            var outDict = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "verb", "unbolt" },
                { "item_form", "" },
                { "mass", 0.0 },
                { "instance_id", "" },
                { "noise", 0.0 },
                { "xp_event", "" },
            };
            if (work == null || placement == null)
            {
                outDict["reason"] = "no_work_or_placement";
                return outDict;
            }
            if (work.Status != WorkActionState.STATUS_COMPLETED)
            {
                outDict["reason"] = "not_completed";
                return outDict;
            }
            string instanceId = work.TargetId ?? "";
            if (instanceId.Length == 0)
            {
                outDict["reason"] = "no_target";
                return outDict;
            }
            GdDict result = placement.Dismount(instanceId);
            if (!V.Bool(result.Get("ok", false)))
            {
                outDict["reason"] = V.Str(result.Get("reason", "dismount_failed"));
                return outDict;
            }
            string itemForm = V.Str(result.Get("item_form", ""));
            long qty = Math.Max(1L, V.I64(result.Get("qty", 1L)));
            inventory[itemForm] = V.I64(inventory.Get(itemForm, 0L)) + qty;
            outDict["ok"] = true;
            outDict["item_form"] = itemForm;
            outDict["mass"] = V.F64(result.Get("mass", 0.0));
            outDict["instance_id"] = instanceId;
            outDict["noise"] = work.Noise();
            outDict["xp_event"] = work.XpEvent();
            outDict["component_id"] = V.Str(result.Get("component_id", ""));
            outDict["linked_system"] = V.Str(result.Get("linked_system", ""));
            outDict["linked_subcomponent"] = V.Str(result.Get("linked_subcomponent", ""));
            return outDict;
        }

        /// <summary>
        /// Complete a mount WorkAction. work.target_id format: room|slot_kind|slot_index|item_form
        /// or pass explicit fields via mount_context.
        /// </summary>
        public static GdDict ResolveMount(
            WorkActionState work,
            IComponentPlacement placement,
            GdDict inventory,
            ComponentCatalog catalog = null,
            GdDict mountContext = null)
        {
            mountContext = mountContext ?? new GdDict();
            var outDict = new GdDict
            {
                { "ok", false },
                { "reason", "" },
                { "verb", "mount" },
                { "item_form", "" },
                { "instance_id", "" },
                { "noise", 0.0 },
                { "xp_event", "" },
            };
            if (work == null || placement == null)
            {
                outDict["reason"] = "no_work_or_placement";
                return outDict;
            }
            if (work.Status != WorkActionState.STATUS_COMPLETED)
            {
                outDict["reason"] = "not_completed";
                return outDict;
            }
            string roomId = V.Str(mountContext.Get("room_id", ""));
            string slotKind = V.Str(mountContext.Get("slot_kind", "wall"));
            long slotIndex = V.I64(mountContext.Get("slot_index", 0L));
            string itemForm = V.Str(mountContext.Get("item_form", ""));
            if (itemForm.Length == 0 || roomId.Length == 0)
            {
                // Parse target_id: room|slot_kind|slot_index|item_form
                string tid = work.TargetId ?? "";
                List<string> parts = GdString.Split(tid, "|");
                if (parts.Count >= 4)
                {
                    roomId = parts[0];
                    slotKind = parts[1];
                    slotIndex = V.I64(parts[2]);
                    itemForm = parts[3];
                }
            }
            if (itemForm.Length == 0 || roomId.Length == 0)
            {
                outDict["reason"] = "bad_target";
                return outDict;
            }
            GdDict result = placement.Mount(itemForm, roomId, slotKind, slotIndex, inventory, catalog);
            if (!V.Bool(result.Get("ok", false)))
            {
                outDict["reason"] = V.Str(result.Get("reason", "mount_failed"));
                return outDict;
            }
            outDict["ok"] = true;
            outDict["item_form"] = itemForm;
            outDict["instance_id"] = V.Str(result.Get("instance_id", ""));
            outDict["noise"] = work.Noise();
            outDict["xp_event"] = work.XpEvent();
            return outDict;
        }
    }
}
