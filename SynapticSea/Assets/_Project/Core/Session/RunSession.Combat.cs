// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: weapon hotbar / attack / reload / armor (7088-7401),
// the consumable pipeline + hotbar (7481-7589), wounds (3850-3935), and the gameplay-input verbs of _input (11826-11859).
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        GdArray InventoryHotbarIds()
        {
            if (InventoryState == null)
                return new GdArray();
            var ids = new GdArray(InventoryState.Items.Keys);
            GdSort.Sort(ids);
            var output = new GdArray();
            foreach (object id in ids)
                output.Add(V.Str(id));
            return output;
        }

        string WeaponAmmoItemId(string weaponId)
        {
            if (ThreatManager == null)
                return "";
            if (ThreatManager.WeaponDefinitions.Get(weaponId, new GdDict()) is GdDict weapon)
                return V.Str(weapon.Get("ammo_item_id", ""));
            return "";
        }

        string EquippedPrimaryWeaponId()
        {
            if (EquipmentState == null || ThreatManager == null)
                return "";
            foreach (string slotId in new[] { "primary_hand", "secondary_hand" })
            {
                string itemId = EquipmentState.GetEquipped(slotId);
                if (ThreatManager.WeaponDefinitions.Has(itemId))
                    return itemId;
            }
            return "";
        }

        GdDict PlayerArmorProfile()
        {
            var profile = new GdDict
            {
                { "flat_reduction", new GdDict() },
                { "resistance", new GdDict() },
                { "durability", 0.0 },
                { "max_durability", 0.0 },
                { "wear_factor", 0.35 },
            };
            if (EquipmentState == null)
                return profile;
            if (EquipmentState.GetEquipped("suit") == "hardsuit")
            {
                profile["resistance"] = new GdDict { { "physical", 0.20 }, { "bleed", 0.15 }, { "fire", 0.25 }, { "electric", 0.20 } };
                profile["durability"] = 40.0;
                profile["max_durability"] = 40.0;
            }
            return profile;
        }

        /// <summary><c>_refresh_weapon_hotbar()</c>: the weapon/ammo/threat text (also saved as <c>combat_hotbar_text</c>).</summary>
        void RefreshWeaponHotbar()
        {
            string weaponId = EquippedPrimaryWeaponId();
            if (weaponId.Length == 0)
            {
                _lastWeaponHotbarText = "Weapon: unarmed | Threat: " + GdString.FormatFixed(ThreatManager != null ? ThreatManager.AwarenessIndicator : 0.0, 2)
                                        + " | Hostiles: " + (ThreatManager != null ? ThreatManager.GetActiveThreatCount() : 0);
                Events.RaiseHotbarText(_lastWeaponHotbarText);
                return;
            }
            string weaponName = ItemDefs.DisplayName(DefinitionsForEquip(), weaponId);
            string ammoItemId = WeaponAmmoItemId(weaponId);
            string ammoText = "melee";
            if (ammoItemId.Length > 0 && InventoryState != null)
                ammoText = ammoItemId + "=" + InventoryState.GetQuantity(ammoItemId);
            string combatText = "idle";
            if (ThreatManager != null)
                combatText = ThreatManager.HasCombatEngagement() ? "combat" : "stealth";
            _lastWeaponHotbarText = weaponName + " | " + ammoText + " | Threat " + GdString.FormatFixed(ThreatManager != null ? ThreatManager.AwarenessIndicator : 0.0, 2) + " | " + combatText;
            Events.RaiseHotbarText(_lastWeaponHotbarText);
        }

        /// <summary><c>attack_primary</c>: attack with the equipped weapon (crowbar fallback); dissipates a phantom in reach.</summary>
        public GdDict AttackWithEquippedWeapon()
        {
            if (ThreatManager == null)
                return new GdDict { { "ok", false }, { "reason", "threat_manager_missing" } };
            string weaponId = EquippedPrimaryWeaponId();
            if (weaponId.Length == 0)
                weaponId = "crowbar";
            GdDict result = ThreatManager.AttackWithWeapon(weaponId, InventoryState, EquipmentState, AmmoState);
            if (result.GetBool("ok"))
            {
                RefreshInventoryHud();
                RefreshPlayerVitals(0.0);
                SyncCurrentShipCombatSummary();
                PlaySfx(AudioEventSeam.SFX_COMBAT_HIT);
            }
            else
            {
                string reason = V.Str(result.Get("reason", ""));
                if (reason == "empty_magazine" || reason == "no_ammo" || reason == "reloading")
                    PlaySfx(AudioEventSeam.SFX_TOOL_USE);
                else
                    PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
            if (HallucinationManager != null)
            {
                Vec3 ppos = HasPlayer ? PlayerPos : Vec3.Zero;
                if (HallucinationManager.DissipatePhantomInRange(ppos))
                {
                    result["phantom_dissipated"] = true;
                    result["ok"] = true;
                    PlaySfx(AudioEventSeam.SFX_COMBAT_HIT);
                    RefreshInventoryHud();
                }
            }
            RefreshWeaponHotbar();
            return result;
        }

        /// <summary><c>reload_weapon</c>: begin a timed reload for the equipped ranged weapon (debits the reserve now).</summary>
        public void BeginWeaponReload()
        {
            if (AmmoState == null || ThreatManager == null)
                return;
            if (AmmoState.IsReloading())
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            string weaponId = EquippedPrimaryWeaponId();
            if (weaponId.Length == 0)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            GdDict weapon = ThreatManager.WeaponDefinitions.Get(weaponId, new GdDict()) as GdDict ?? new GdDict();
            string ammoItemId = V.Str(weapon.Get("ammo_item_id", ""));
            if (ammoItemId.Length == 0)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            long magSize = V.I64(weapon.Get("magazine_size", 0L));
            long reserve = InventoryState != null ? InventoryState.GetQuantity(ammoItemId) : 0;
            if (AmmoState.BeginReload(weaponId, magSize, reserve))
            {
                InventoryState?.RemoveItem(ammoItemId, AmmoState.ReloadTarget);
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
                RefreshInventoryHud();
                RefreshWeaponHotbar();
            }
            else
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
        }

        // ------------------------------------------------------------------ consumables
        Dictionary<string, object> ConsumablePipelineContext() => new Dictionary<string, object>
        {
            { "effect_dispatcher", EffectDispatcher },
            { "consumable_state", ConsumableState },
            { "medicine_state", MedicineState },
            { "stimulant_state", StimulantState },
            { "addiction_state", AddictionState },
            { "ammo_state", AmmoState },
            { "utility_state", UtilityItemState },
            { "vitals_state", VitalsState },
            { "sanity_state", SanityState },
            { "radiation_state", RadiationState },
            { "body_temperature_state", BodyTemperatureState },
            { "status_effects_state", StatusEffectsState },
            { "spoilage_state", SpoilageState },
        };

        GdArray GetConsumableSlotLabels()
        {
            var output = new GdArray();
            if (ConsumableState == null)
                return output;
            foreach (string itemId in ConsumableState.HotbarSlots)
            {
                if (string.IsNullOrEmpty(itemId))
                {
                    output.Add("(empty)");
                }
                else
                {
                    long qty = InventoryState != null ? InventoryState.GetQuantity(itemId) : 0;
                    output.Add(ItemDefs.DisplayName(DefinitionsForEquip(), itemId) + " x" + qty);
                }
            }
            return output;
        }

        void RefreshConsumableUi(long selectedIndex = 0)
        {
            Events.RaiseInventoryItems(InventoryHotbarIds());
            Events.RaiseHotbarSlots(GetConsumableSlotLabels(), selectedIndex);
        }

        void EnsureConsumableHotbarAssignments()
        {
            if (ConsumableState == null || InventoryState == null)
                return;
            var usable = new List<string>();
            foreach (object idObj in InventoryHotbarIds())
            {
                string itemId = V.Str(idObj);
                if (InventoryState.GetQuantity(itemId) > 0 && ConsumableState.HasUseAction(itemId))
                    usable.Add(itemId);
            }
            for (int slotIndex = 0; slotIndex < ConsumableState.HotbarSlots.Count; slotIndex++)
            {
                string current = ConsumableState.HotbarSlots[slotIndex] ?? "";
                if (current.Length == 0 || InventoryState.GetQuantity(current) <= 0 || !ConsumableState.HasUseAction(current))
                    ConsumableState.AssignHotbarSlot(slotIndex, slotIndex < usable.Count ? usable[slotIndex] : "");
            }
        }

        /// <summary>Use an inventory item through the consumable pipeline (inventory panel "use").</summary>
        public GdDict UseConsumableItem(string itemId, bool useAll = false)
        {
            if (ConsumableState == null || InventoryState == null)
                return new GdDict { { "ok", false }, { "reason", "consumable_pipeline_missing" } };
            GdDict result = ConsumableState.UseItem(itemId, InventoryState, ConsumablePipelineContext(), useAll);
            if (result.GetBool("ok"))
            {
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
                EnsureConsumableHotbarAssignments();
                RecomputePlayerEncumbrance();
                RefreshOxygenState(false, 0.0);
                RefreshPlayerVitals(0.0);
                RefreshConsumableUi();
                EmitConsumableTraining(itemId);
            }
            else
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
            return result;
        }

        /// <summary>Hotbar keys 1-3.</summary>
        public GdDict UseConsumableHotbarSlot(long slotIndex)
        {
            if (ConsumableState == null || InventoryState == null)
                return new GdDict { { "ok", false }, { "reason", "consumable_pipeline_missing" } };
            string usedId = "";
            if (slotIndex >= 0 && slotIndex < ConsumableState.HotbarSlots.Count)
                usedId = ConsumableState.HotbarSlots[(int)slotIndex] ?? "";
            GdDict result = ConsumableState.UseHotbarSlot(slotIndex, InventoryState, ConsumablePipelineContext());
            if (result.GetBool("ok"))
            {
                PlaySfx(AudioEventSeam.SFX_TOOL_USE);
                EnsureConsumableHotbarAssignments();
                RecomputePlayerEncumbrance();
                RefreshOxygenState(false, 0.0);
                RefreshPlayerVitals(0.0);
                RefreshConsumableUi(slotIndex);
                string trainId = V.Str(result.Get("item_id", usedId));
                if (trainId.Length > 0)
                    EmitConsumableTraining(trainId);
            }
            else
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            }
            return result;
        }

        /// <summary>Assign a hotbar slot (was <c>assign_hotbar_slot_for_validation</c>).</summary>
        public bool AssignHotbarSlot(long slotIndex, string itemId)
        {
            if (ConsumableState == null)
                return false;
            bool ok = ConsumableState.AssignHotbarSlot(slotIndex, itemId);
            if (ok)
                RefreshConsumableUi(slotIndex);
            return ok;
        }

        void EmitConsumableTraining(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || InventoryState == null)
                return;
            string cat = InventoryState.GetCategory(itemId);
            if (cat == "medicine")
                EmitTrainingEvent("first_aid_self", itemId);
            else if (cat == "food" || cat == "drink")
                EmitTrainingEvent("ration_supplies", itemId);
        }

        /// <summary>A transfer/equip in the inventory panel mutated player state.</summary>
        public void OnInventoryTransferCompleted()
        {
            RecomputePlayerEncumbrance();
            EnsureConsumableHotbarAssignments();
            RefreshOxygenState(false, 0.0);
            RefreshConsumableUi();
            RefreshWeaponHotbar();
            PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
        }

        void RefreshUiShellRuntime()
        {
            Events.RaiseLoadAvailable(IsLoadAvailable());
            Events.RaiseInventoryItems(InventoryHotbarIds());
            Events.RaiseHotbarSlots(GetConsumableSlotLabels(), 0);
            RefreshWeaponHotbar();
        }

        // ------------------------------------------------------------------ wounds
        string FirstInventoryItem(IReadOnlyList<string> itemIds)
        {
            if (InventoryState == null)
                return "";
            foreach (string id in itemIds)
            {
                if (InventoryState.GetQuantity(id) > 0)
                    return id;
            }
            return "";
        }

        /// <summary>Bandage a wound if the inventory holds a bandage item (consumes 1); the panel's selection is passed in.</summary>
        public bool TryBandageWound(string woundId)
        {
            if (WoundState == null || InventoryState == null)
                return false;
            string itemId = FirstInventoryItem(BANDAGE_ITEM_IDS);
            if (itemId.Length == 0)
                return false;
            if (!WoundState.Bandage(woundId))
                return false;
            InventoryState.RemoveItem(itemId, 1);
            PlaySfx(AudioEventSeam.SFX_WOUND_BANDAGE);
            EmitTrainingEvent("bandage_wound", woundId);
            return true;
        }

        /// <summary>Treat a wound if the inventory holds a medical item (consumes 1).</summary>
        public bool TryTreatWound(string woundId)
        {
            if (WoundState == null || InventoryState == null)
                return false;
            string itemId = FirstInventoryItem(TREAT_ITEM_IDS);
            if (itemId.Length == 0)
                return false;
            if (!WoundState.Treat(woundId, 0.35))
                return false;
            InventoryState.RemoveItem(itemId, 1);
            PlaySfx(AudioEventSeam.SFX_WOUND_TREAT);
            EmitTrainingEvent("treat_wound", woundId);
            return true;
        }
    }
}
