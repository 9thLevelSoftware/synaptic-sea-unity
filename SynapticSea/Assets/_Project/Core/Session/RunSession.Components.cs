using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        const string ComponentPlayerHolder = "player:player_local";
        DomainTransactionCoordinator _componentDomain;
        bool _componentPublishing, _componentMutating;
        string _componentOpenHolder = "";
        double _componentNoiseAcc;
        public bool ComponentIntegrationEnabled => Deps.EnableComponentIntegration;
        internal bool ComponentPublicationInProgress => _componentPublishing || _componentMutating || ComponentGenerationRestoreInProgress;
        public event Action<GdDict> ComponentDomainChanged;
        /// <summary>Diagnostic fault seam; does not alter ordinary gameplay policy.</summary>
        public Action<string> ComponentStageHook;

        static GdDict ComponentFailure(string reason) => new GdDict { { "ok", false }, { "committed", false }, { "reason", reason } };
        static GdDict Instances(GdDict summary) => summary.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
        static string SlotHolder(string shipId, string slotId) => "slot:" + shipId + ":" + slotId;
        static string CargoHolder(string shipId) => "ship_cargo:" + shipId;
        static string CartHolder(string shipId, string cartId) => "cart:" + shipId + ":" + cartId;
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal void InitializeComponentIntegration()
        {
            if (!ComponentIntegrationEnabled || ComponentGenerationRestoreInProgress || HomeShip == null || ComponentCatalog == null) return;
            if (_componentDomain == null)
            {
                var initial = new GdDict { { "schema_version", 2L }, { "revision", 0L },
                    { "registry", new GdDict { { "schema_version", 1L }, { "instances", new GdDict() } } },
                    { "holders", new GdDict { { ComponentPlayerHolder, NewHolder(ComponentPlayerHolder, "player", PLAYER_LOCAL_ID) } } },
                    { "machinery", new GdDict() }, { "receipts", new GdDict() }, { "physical_slots", new GdDict() },
                    { "component_work", new GdDict() }, { "participating_state", ReadComponentParticipants() },
                    { "command_sequence", 0L }, { "registered_owners", new GdArray() } };
                foreach (ShipInstance ship in AllKnownShips()) RegisterComponentShip(initial, ship);
                _componentDomain = NewComponentOwner(initial);
            }
            else RegisterNewComponentOwners();
            BindComponentReadViews();
            ProjectComponentPlacement(_componentDomain.GetSummary());
        }

        internal void BindComponentIntegrationForCurrentShip()
        {
            if (!ComponentIntegrationEnabled || ComponentGenerationRestoreInProgress || HomeShip == null) return;
            InitializeComponentIntegration();
            if (_componentDomain != null) ProjectComponentPlacement(_componentDomain.GetSummary());
        }

        DomainTransactionCoordinator NewComponentOwner(GdDict summary) => new DomainTransactionCoordinator(summary,
            stage => ComponentStageHook?.Invoke(stage), result => NotifyComponentPublication(result), ApplyComponentViews);

        bool EnsureComponentOwner()
        {
            if (ComponentGenerationRestoreInProgress) return false;
            if (!ComponentIntegrationEnabled) return false;
            if (_componentDomain == null) InitializeComponentIntegration();
            return _componentDomain != null;
        }

        static GdDict NewHolder(string id, string kind, string owner) => new GdDict {
            { "holder_id", id }, { "kind", kind }, { "owner_id", owner }, { "revision", 0L } };

        GdDict ComponentLayout(ShipInstance ship)
        {
            if (ship?.BuiltLayout != null && !ship.BuiltLayout.IsEmpty) return ship.BuiltLayout;
            if (ship?.SceneRoot is IShipLoaderView loader) return loader.GetLayoutCopy();
            if (ship == HomeShip && Loader != null) return Loader.GetLayoutCopy();
            return new GdDict();
        }

        /// <summary>Proves archived physical holders against the same layout/catalog derivation used at registration.</summary>
        public static bool ValidateComponentPhysicalLayout(GdDict domain, string owner, GdDict layout, out string reason)
        {
            reason = "invalid_component_physical_layout";
            if (domain == null || layout == null || string.IsNullOrWhiteSpace(owner) ||
                !(domain.Get("schema_version") is long schema) || (schema != 2 && schema != 3 && schema != 4) ||
                !domain.GetArrayOrEmpty("registered_owners").Contains(owner)) return false;
            var catalog = new ComponentCatalog();
            if (!catalog.LoadDefault()) { reason = "component_content_missing"; return false; }
            if (!TryDescribeComponentPhysicalLayout(owner, layout, catalog, out GdDict expected)) return false;
            GdDict actual = domain.GetDictOrEmpty("physical_slots"), holders = domain.GetDictOrEmpty("holders");
            var actualSlots = new GdDict();
            foreach (var pair in actual)
                if (pair.Value is GdDict row && (row.GetString("ship_id") == owner || V.Str(pair.Key).StartsWith("slot:" + owner + ":", StringComparison.Ordinal)))
                    actualSlots[pair.Key] = row;
            if (!V.VariantEquals(expected, actualSlots)) return false;
            var ownerHolders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in holders)
            {
                if (!(pair.Value is GdDict holder) || holder.GetString("kind") != "slot" || holder.GetString("owner_id") != owner) continue;
                string id = V.Str(pair.Key); ownerHolders.Add(id);
                GdDict physical = expected.Get(id) as GdDict;
                if (physical == null || holder.GetString("holder_id") != id || holder.GetString("slot_id") != physical.GetString("slot_id") ||
                    !V.VariantEquals(holder.Get("accepted_forms"), physical.Get("accepted_forms"))) return false;
            }
            if (!ownerHolders.SetEquals(expected.Keys.Select(V.Str))) return false;
            reason = "ok"; return true;
        }

        static bool TryDescribeComponentPhysicalLayout(string owner, GdDict layout, ComponentCatalog catalog, out GdDict slots)
        {
            slots = new GdDict();
            var placement = new ComponentPlacementState();
            foreach (GdDict physical in placement.DescribePhysicalSlots(layout, catalog).OfType<GdDict>())
            {
                if (physical.GetArrayOrEmpty("accepted_forms").IsEmpty) continue;
                GdDict room = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().FirstOrDefault(r => r.GetString("id") == physical.GetString("room_id"));
                GdArray cell = LayoutSerializer.ParseSlotCell(physical.Get("cell"));
                if (room == null || cell.Count < 2) continue;
                if (!TryComponentFloorAnchor(layout, room, cell, room.GetInt("deck"), out Vec3 anchor)) return false;
                string id = SlotHolder(owner, physical.GetString("slot_id"));
                if (slots.Has(id)) return false;
                GdDict metadata = physical.DeepCopy();
                metadata["holder_id"] = id; metadata["ship_id"] = owner; metadata["local_position"] = anchor;
                slots[id] = metadata;
            }
            return true;
        }

        static bool TryComponentFloorAnchor(GdDict layout, GdDict room, GdArray cell, long deck, out Vec3 anchor)
        {
            anchor = Vec3.Inf;
            string cellKey = deck + "|" + V.I64(cell[0]) + "|" + V.I64(cell[1]);
            foreach (GdDict floor in layout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements").OfType<GdDict>())
                if (floor.GetString("cell_key") == cellKey && floor.GetString("room_id") == room.GetString("id"))
                    return TryComponentPositionAnchor(floor, out anchor);
            string name1 = "floor_cell_x" + V.I64(cell[0]) + "_z" + V.I64(cell[1]);
            string name2 = "floor_cell_d" + deck + "_x" + V.I64(cell[0]) + "_z" + V.I64(cell[1]);
            foreach (GdDict floor in room.GetArrayOrEmpty("structural_placements").OfType<GdDict>())
                if (floor.GetString("name") == name1 || floor.GetString("name") == name2)
                    return TryComponentPositionAnchor(floor, out anchor);
            return false;
        }

        static bool TryComponentPositionAnchor(GdDict floor, out Vec3 anchor)
        {
            anchor = Vec3.Inf;
            // Validate original tokens before any legacy prefix parser can discard malformed exponent text.
            object original = floor.Get("position", floor.Get("world_position"));
            if (original == null) original = floor.Get("world_position");
            GdArray position = original as GdArray;
            if (original is Vec3 vector) position = GdArray.Of((double)vector.X, (double)vector.Y, (double)vector.Z);
            else if (original is string serialized)
            {
                string text = serialized.Trim();
                if (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal))
                    text = text.Substring(1, text.Length - 2);
                string[] tokens = text.Split(',');
                if (tokens.Length != 3) return false;
                position = GdArray.Of(tokens[0], tokens[1], tokens[2]);
            }
            if (position == null || position.Count != 3) return false;
            var coordinates = new double[3];
            for (int index = 0; index < 3; index++)
            {
                object value = position[index];
                if (value is string token)
                {
                    if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out coordinates[index])) return false;
                }
                else if (V.IsNumber(value)) coordinates[index] = V.F64(value);
                else return false;
                if (!Finite(coordinates[index])) return false;
            }
            anchor = new Vec3(coordinates[0], coordinates[1] + 0.12, coordinates[2]);
            return Finite(anchor.X) && Finite(anchor.Y) && Finite(anchor.Z);
        }

        void RegisterComponentShip(GdDict domain, ShipInstance ship)
        {
            if (ship == null || domain.GetArrayOrEmpty("registered_owners").Contains(ship.ShipId)) return;
            if (string.IsNullOrWhiteSpace(ship.ShipId)) throw new InvalidOperationException("component_owner_missing");
            GdDict layout = ComponentLayout(ship);
            GdDict holders = domain.GetDictOrEmpty("holders"), machines = domain.GetDictOrEmpty("machinery");
            GdDict slots = domain.GetDictOrEmpty("physical_slots"), instances = Instances(domain);
            string cargo = CargoHolder(ship.ShipId);
            holders[cargo] = NewHolder(cargo, "ship_cargo", ship.ShipId);
            holders.GetDictOrEmpty(cargo)["capacity_mass"] = ship.GetInventory().MaxWeight;
            foreach (CartState cart in ship.GetCarts())
            {
                string cartId = CartHolder(ship.ShipId, cart.CartId);
                holders[cartId] = NewHolder(cartId, "cart", ship.ShipId);
                holders.GetDictOrEmpty(cartId)["capacity_mass"] = cart.GetHold().MaxWeight;
            }
            GdArray placed = ship == CurrentShip && ComponentPlacementState != null ? ComponentPlacementState.Placed.DeepCopy()
                : ship.ComponentPlacementSummary.GetArrayOrEmpty("placed").DeepCopy();
            if (!TryDescribeComponentPhysicalLayout(ship.ShipId, layout, ComponentCatalog, out GdDict physicalSlots))
                throw new InvalidOperationException("component_physical_witness_missing");
            foreach (GdDict metadata in physicalSlots.Values.OfType<GdDict>())
            {
                GdArray forms = metadata.GetArrayOrEmpty("accepted_forms");
                string slotId = metadata.GetString("slot_id"), holderId = metadata.GetString("holder_id");
                GdDict holder = NewHolder(holderId, "slot", ship.ShipId);
                holder["slot_id"] = slotId; holder["accepted_forms"] = forms.DeepCopy();
                holders[holderId] = holder;
                slots[holderId] = metadata;
            }
            foreach (GdDict original in placed.OfType<GdDict>())
            {
                if (!original.GetBool("mounted", true)) throw new InvalidOperationException("legacy_component_identity_ambiguous");
                string localId = original.GetString("component_instance_id"), definitionId = original.GetString("component_id");
                string slotId = original.GetString("room_id") + "|" + original.GetString("slot_kind") + "|" + original.GetInt("slot_index");
                string holderId = SlotHolder(ship.ShipId, slotId), instanceId = ship.ShipId + ":" + localId;
                GdDict definition = ComponentCatalog.GetComponent(definitionId);
                double condition = original.GetFloat("condition", double.NaN), mass = original.GetFloat("mass", double.NaN);
                if (localId.Length == 0 || definition.IsEmpty || !holders.Has(holderId) || instances.Has(instanceId) ||
                    !Finite(condition) || condition < 0 || condition > 1 || !Finite(mass) || mass <= 0)
                    throw new InvalidOperationException("legacy_component_evidence_missing");
                instances[instanceId] = new GdDict { { "schema_version", 1L }, { "instance_id", instanceId },
                    { "definition_id", definitionId }, { "item_form", original.GetString("item_form") }, { "condition_state", "known" },
                    { "condition", condition }, { "mass", mass }, { "holder", holderId }, { "revision", 0L },
                    { "origin", new GdDict { { "ship_id", ship.ShipId }, { "local_instance_id", localId }, { "slot_id", slotId } } },
                    { "provenance", new GdDict { { "kind", "actual_generation_placement" } } } };
                string system = original.GetString("linked_system"), subId = original.GetString("linked_subcomponent");
                if (system.Length == 0 || subId.Length == 0) continue;
                ShipSubcomponent sub = ship.SystemsManager?.GetSystem(system)?.GetSubcomponent(subId);
                if (sub == null || !Finite(sub.Health)) throw new InvalidOperationException("component_machinery_evidence_missing");
                string machineId = ship.ShipId + ":" + system + ":" + subId;
                holders.GetDictOrEmpty(holderId)["machinery_id"] = machineId;
                if (!machines.Has(machineId)) machines[machineId] = new GdDict { { "machinery_id", machineId }, { "owner_id", ship.ShipId },
                    { "health", sub.Health }, { "system_id", system }, { "subcomponent_id", subId } };
                else if (machines.GetDictOrEmpty(machineId).GetFloat("health") != sub.Health)
                    throw new InvalidOperationException("component_machinery_alias_conflict");
            }
            domain.GetArrayOrEmpty("registered_owners").Add(ship.ShipId);
        }

        void RegisterNewComponentOwners()
        {
            if (!ComponentIntegrationEnabled) return;
            if (_componentDomain == null || _componentMutating) return;
            GdDict before = _componentDomain.GetSummary(), candidate = before.DeepCopy();
            foreach (ShipInstance ship in AllKnownShips()) RegisterComponentShip(candidate, ship);
            if (V.VariantEquals(before, candidate)) return;
            candidate["revision"] = checked(before.GetInt("revision") + 1);
            if (!_componentDomain.ApplySummary(candidate)) throw new InvalidOperationException("component_owner_registration_failed");
        }

        GdDict ReadComponentParticipants(GdDict detachedPaidState = null)
        {
            var stacks = new GdDict();
            foreach (ShipInstance ship in AllKnownShips())
            {
                stacks[CargoHolder(ship.ShipId)] = ship.GetInventory().GetSummary();
                foreach (CartState cart in ship.GetCarts()) stacks[CartHolder(ship.ShipId, cart.CartId)] = cart.GetHold().GetSummary();
            }
            var participants = new GdDict { { "inventory", InventoryState?.GetSummary() ?? new GdDict() },
                { "progression", PlayerProgression?.GetSummary() ?? new GdDict() }, { "training", TrainingEventBus?.ToDict() ?? new GdDict() },
                { "crafting", CraftingState?.GetSummary() ?? new GdDict() }, { "field_crafting", FieldCraftingState?.GetSummary() ?? new GdDict() },
                { "stacks", stacks } };
            return PaidCraftingEnabled ? ReadPaidParticipants(participants, detachedPaidState) : participants;
        }

        void RefreshComponentParticipants()
        {
            if (ComponentGenerationRestoreInProgress || _componentMutating || _componentPublishing || _componentDomain == null) return;
            RegisterNewComponentOwners();
            GdDict before = _componentDomain.GetSummary(), candidate = before.DeepCopy();
            candidate["participating_state"] = ReadComponentParticipants(PaidCraftingEnabled ? PaidState(candidate) : null);
            foreach (ShipInstance ship in AllKnownShips())
            {
                RefreshComponentCapacity(candidate, CargoHolder(ship.ShipId), ship.GetInventory().MaxWeight);
                foreach (CartState cart in ship.GetCarts()) RefreshComponentCapacity(candidate, CartHolder(ship.ShipId, cart.CartId), cart.GetHold().MaxWeight);
            }
            foreach (GdDict machine in candidate.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
            {
                ShipSubcomponent sub = FindShipById(machine.GetString("owner_id"))?.SystemsManager?.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id"));
                if (sub != null) machine["health"] = sub.Health;
            }
            if (V.VariantEquals(before, candidate)) return;
            candidate["revision"] = checked(before.GetInt("revision") + 1);
            if (!_componentDomain.ApplySummary(candidate)) throw new InvalidOperationException("component_capture_invalid");
            if (PaidCraftingEnabled && RecipeKnowledge != null) RecipeKnowledge.ApplySummary(PaidState(candidate).GetDictOrEmpty("knowledge"));
        }

        public GdDict CaptureComponentDomain()
        {
            if (ComponentGenerationRestoreInProgress) return ComponentFailure("restore_in_progress");
            try
            {
                if (!EnsureComponentOwner()) return new GdDict();
                if (!_componentPublishing) RefreshComponentParticipants();
                return _componentDomain.GetSummary();
            }
            catch (OverflowException) { return ComponentFailure("revision_overflow"); }
        }

        static bool ComponentCaptureFailed(GdDict domain) => domain.Get("ok") is bool ok && !ok;

        static void RefreshComponentCapacity(GdDict domain, string id, double capacity)
        {
            GdDict holder = domain.GetDictOrEmpty("holders").GetDictOrEmpty(id);
            if (holder.IsEmpty || holder.GetFloat("capacity_mass") == capacity) return;
            holder["capacity_mass"] = capacity; holder["revision"] = checked(holder.GetInt("revision") + 1);
        }

        public bool ValidateComponentDomainRestore(GdDict summary, out string reason)
        {
            reason = "component_integration_inactive";
            if (!ComponentIntegrationEnabled || summary?.GetInt("schema_version") == 4 && !ManualStudyEnabled) return false;
            if (!DomainBundle.TryCreate(summary, out _, out reason) || (summary.GetInt("schema_version") != 2 && !(PaidCraftingEnabled && PaidCraftingState.IsDomainVersion(summary.GetInt("schema_version")) && summary.GetString("domain_mode") == "components_and_craft"))) return false;
            foreach (GdDict row in Instances(summary).Values.OfType<GdDict>())
            {
                if (row.GetString("condition_state") != "known") { reason = "legacy_resolution_required"; return false; }
                GdDict definition = ComponentCatalog?.GetComponent(row.GetString("definition_id"));
                if (definition == null || definition.IsEmpty || definition.GetString("item_form") != row.GetString("item_form") ||
                    !summary.GetArrayOrEmpty("registered_owners").Contains(summary.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("owner_id")))
                {
                    // Player storage is a deliberate different owner from the physical origin.
                    if (definition == null || definition.IsEmpty || definition.GetString("item_form") != row.GetString("item_form") || row.GetString("holder") != ComponentPlayerHolder)
                    { reason = "component_content_or_owner_missing"; return false; }
                }
            }
            GdDict participants = summary.GetDictOrEmpty("participating_state");
            foreach (var item in participants.GetDictOrEmpty("inventory").GetDictOrEmpty("items"))
                if (IsComponentForm(V.Str(item.Key)) && V.I64(item.Value) > 0) { reason = "diagnostic_anonymous_component_quantity"; return false; }
            foreach (GdDict stack in participants.GetDictOrEmpty("stacks").Values.OfType<GdDict>())
                foreach (var item in stack.GetDictOrEmpty("items"))
                    if (IsComponentForm(V.Str(item.Key)) && V.I64(item.Value) > 0) { reason = "diagnostic_anonymous_component_quantity"; return false; }
            var training = new TrainingEventBus(); training.Configure();
            if (!training.ApplySummary(participants.GetDictOrEmpty("training"))) { reason = "invalid_component_training"; return false; }
            var machineCatalog = new ShipSystemsManager(); machineCatalog.Configure(machineCatalog.LoadDefinitions(), ShipSystemsManager.CONDITION_PRISTINE, 0L);
            foreach (GdDict machine in summary.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
                if (machine.GetString("system_id").Length == 0 || machine.GetString("subcomponent_id").Length == 0 ||
                    !summary.GetArrayOrEmpty("registered_owners").Contains(machine.GetString("owner_id")) ||
                    machineCatalog.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id")) == null ||
                    machine.GetString("machinery_id") != machine.GetString("owner_id") + ":" + machine.GetString("system_id") + ":" + machine.GetString("subcomponent_id"))
                { reason = "component_machinery_evidence_missing"; return false; }
            reason = "ok"; return true;
        }

        public bool RestoreComponentDomain(GdDict summary)
        {
            if (ComponentGenerationRestoreInProgress) return false;
            return RestoreComponentDomainOwned(summary);
        }

        // Only the selected-generation publisher may restore while public mutation admission is closed.
        bool RestoreComponentDomainOwned(GdDict summary)
        {
            if (PaidCraftingEnabled && summary != null && PaidCraftingState.IsDomainVersion(summary.GetInt("schema_version"))) return RestorePaidCraftingDomain(summary);
            if (_componentMutating || _componentPublishing || !ValidateComponentDomainRestore(summary, out _)) return false;
            GdDict candidate = summary.DeepCopy();
            GdDict work = candidate.GetDictOrEmpty("component_work");
            if (!work.IsEmpty && work.GetString("status") != "committed")
            { work["status"] = "paused_restore"; work["resume_required"] = true; work["reason"] = "explicit_resume_required"; }
            DomainTransactionCoordinator next = NewComponentOwner(candidate);
            try
            {
                ApplyComponentViews(candidate);
                _componentDomain = next;
                BindComponentReadViews();
                ProjectComponentPlacement(candidate);
                _workHoldInput = false;
                _workAwaitingResume = !work.IsEmpty && work.GetBool("resume_required");
                MirrorComponentWork(work);
                return true;
            }
            catch { return false; }
        }

        void BindComponentReadViews()
        {
            if (_componentDomain == null || !ComponentIntegrationEnabled) return;
            if (CraftingState != null) CraftingState.RecipePreflight = ComponentRecipePreflight;
            if (FieldCraftingState != null) FieldCraftingState.RecipePreflight = ComponentRecipePreflight;
            if (InventoryState != null)
            {
                InventoryState.ComponentMass = () => ComponentMassFor(ComponentPlayerHolder);
                InventoryState.RejectAnonymousComponent = IsComponentForm;
            }
            if (ShipModificationState != null) ShipModificationState.RequireInstanceOwner = true;
            if (ComponentPlacementState != null) ComponentPlacementState.RequireInstanceOwner = true;
            foreach (ShipInstance ship in AllKnownShips())
            {
                string id = CargoHolder(ship.ShipId);
                ship.GetInventory().ComponentMass = () => ComponentMassFor(id);
                ship.GetInventory().RejectAnonymousComponent = IsComponentForm;
                foreach (CartState cart in ship.GetCarts())
                {
                    string cartId = CartHolder(ship.ShipId, cart.CartId);
                    cart.GetHold().ComponentMass = () => ComponentMassFor(cartId);
                    cart.GetHold().RejectAnonymousComponent = IsComponentForm;
                }
            }
            foreach (GdDict machine in _componentDomain.GetSummary().GetDictOrEmpty("machinery").Values.OfType<GdDict>())
            {
                ShipSubcomponent sub = FindShipById(machine.GetString("owner_id"))?.SystemsManager?.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id"));
                if (sub == null) continue;
                string machineId = machine.GetString("machinery_id");
                sub.ComponentConditionCap = () => ComponentMachineCap(machineId);
            }
            if (PaidCraftingEnabled && PaidCraftingState.IsDomainVersion(_componentDomain.GetSummary().GetInt("schema_version"))) BindPaidCraftingModels();
        }

        double ComponentMassFor(string holderId)
            => _componentDomain == null ? 0 : _componentDomain.GetProjections().GetDictOrEmpty("holders").GetDictOrEmpty(holderId).GetFloat("mass");

        bool IsComponentForm(string itemId) => !string.IsNullOrEmpty(itemId) && ComponentCatalog != null && ComponentCatalog.ComponentIdForItemForm(itemId).Length > 0;

        string ComponentRecipePreflight(GdDict recipe)
        {
            if (!ComponentIntegrationEnabled) return "";
            if (ComponentTerminalPending) return "terminal_pending";
            if (IsComponentForm(recipe.GetDictOrEmpty("produces").GetString("item_id")) ||
                recipe.GetDictOrEmpty("ingredients").Keys.Any(id => IsComponentForm(V.Str(id))))
                return "diagnostic_component_crafting_unavailable";
            return "";
        }

        double ComponentMachineCap(string machineId)
        {
            if (_componentDomain == null) return 0;
            GdDict domain = _componentDomain.GetSummary();
            double cap = 1;
            bool linked = false;
            foreach (GdDict holder in domain.GetDictOrEmpty("holders").Values.OfType<GdDict>())
            {
                if (holder.GetString("machinery_id") != machineId) continue;
                linked = true;
                GdDict row = Instances(domain).Values.OfType<GdDict>().SingleOrDefault(instance => instance.GetString("holder") == holder.GetString("holder_id"));
                if (row == null || row.GetString("condition_state") != "known") return 0;
                cap = Math.Min(cap, row.GetFloat("condition"));
            }
            return linked ? cap : 0;
        }

        public GdArray ListComponentInstances(string holderId)
        {
            var rows = new GdArray();
            if (!EnsureComponentOwner()) return rows;
            GdDict domain = CaptureComponentDomain();
            foreach (GdDict original in Instances(domain).Values.OfType<GdDict>().OrderBy(row => row.GetString("instance_id"), StringComparer.Ordinal))
            {
                if (original.GetString("holder") != holderId) continue;
                GdDict row = original.DeepCopy(), holder = domain.GetDictOrEmpty("holders").GetDictOrEmpty(holderId);
                row["name"] = ComponentCatalog.GetComponent(row.GetString("definition_id")).GetString("name", row.GetString("item_form"));
                if (holder.GetString("kind") == "slot") { row["ship_id"] = holder.GetString("owner_id"); row["slot_id"] = holder.GetString("slot_id"); }
                rows.Add(row);
            }
            return rows;
        }

        Vec3 PhysicalWorld(GdDict slot)
        {
            Vec3 local = slot.Get("local_position") is Vec3 vector ? vector : Vec3.Inf;
            ShipInstance ship = FindShipById(slot.GetString("ship_id"));
            return ship != null && RootValid(ship.SceneRoot) ? ToGlobal(ship.SceneRoot, local) : Vec3.Inf;
        }

        bool ComponentSight(Vec3 anchor) => HasPlayer && anchor != Vec3.Inf &&
            (Deps.LosProbe == null || !Deps.LosProbe.HasSpace || !Deps.LosProbe.IntersectRay(PlayerPos + Vec3.Up, anchor + Vec3.Up, out _));

        bool ComponentWrench() => InventoryState != null && (InventoryState.GetQuantity("wrench") > 0 || InventoryState.GetQuantity("tool_wrench") > 0);

        GdDict TargetRow(GdDict domain, GdDict slot, GdDict instance, bool browsing)
        {
            GdDict row = slot.DeepCopy(), holder = domain.GetDictOrEmpty("holders").GetDictOrEmpty(slot.GetString("holder_id"));
            GdDict occupant = Instances(domain).Values.OfType<GdDict>().FirstOrDefault(item => item.GetString("holder") == slot.GetString("holder_id"));
            Vec3 anchor = PhysicalWorld(slot);
            bool inRange = HasPlayer && anchor != Vec3.Inf && PlayerPos.DistanceTo(anchor) <= WORK_ACTION_INTERACT_RANGE;
            bool sight = ComponentSight(anchor), access = CurrentShip?.ShipId == slot.GetString("ship_id");
            long skill = PlayerProgression?.GetSkillLevel("salvage") ?? 0;
            GdDict definition = WorkActionDriver?.Catalog?.GetAction("mount_component") ?? new GdDict();
            bool skillOk = skill >= definition.GetInt("min_skill_level");
            double draw = instance == null ? 0 : ComponentCatalog.GetComponent(instance.GetString("definition_id")).GetFloat("power_draw");
            double supply = ShipModificationState?.PowerSupply ?? 100;
            double demand = ShipModificationState?.PowerDemandBaseline ?? 0;
            foreach (GdDict installed in Instances(domain).Values.OfType<GdDict>())
                if (domain.GetDictOrEmpty("holders").GetDictOrEmpty(installed.GetString("holder")).GetString("kind") == "slot" &&
                    domain.GetDictOrEmpty("holders").GetDictOrEmpty(installed.GetString("holder")).GetString("owner_id") == slot.GetString("ship_id"))
                    demand += ComponentCatalog.GetComponent(installed.GetString("definition_id")).GetFloat("power_draw");
            bool powerOk = occupant != null && instance != null && occupant.GetString("instance_id") == instance.GetString("instance_id") || demand + draw <= supply;
            var requirements = new GdDict { { "in_range", inRange }, { "has_los", sight }, { "access", access },
                { "interaction_range", WORK_ACTION_INTERACT_RANGE }, { "tool", ComponentWrench() }, { "skill", skillOk }, { "power", powerOk } };
            string reason = "ok";
            if (browsing) reason = "selection_required";
            else if (instance == null) reason = "missing_instance";
            else if (instance.GetString("condition_state") != "known") reason = "condition_unknown";
            else if (!holder.GetArrayOrEmpty("accepted_forms").Contains(instance.Get("item_form"))) reason = "form_incompatible";
            else if (occupant != null && occupant.GetString("instance_id") != instance.GetString("instance_id")) reason = "slot_occupied";
            else if (!access) reason = "wrong_owner";
            else if (!inRange) reason = "out_of_range";
            else if (!sight) reason = "line_of_sight";
            else if (!ComponentWrench()) reason = "missing_tool";
            else if (!skillOk) reason = "insufficient_skill";
            else if (!powerOk) reason = "power_budget";
            row["world_position"] = anchor; row["occupied"] = occupant != null; row["instance_id"] = occupant?.GetString("instance_id") ?? "";
            row["machinery_id"] = holder.GetString("machinery_id"); row["requirements"] = requirements; row["ok"] = reason == "ok"; row["reason"] = reason;
            return row;
        }

        public GdArray ListInstallTargets(string instanceId)
        {
            var result = new GdArray();
            if (!EnsureComponentOwner()) return result;
            GdDict domain = CaptureComponentDomain();
            GdDict instance = Instances(domain).Get(instanceId ?? "") as GdDict;
            foreach (GdDict slot in domain.GetDictOrEmpty("physical_slots").Values.OfType<GdDict>().OrderBy(s => s.GetString("holder_id"), StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(instanceId) && slot.GetString("ship_id") != CurrentShip?.ShipId) continue;
                result.Add(TargetRow(domain, slot, instance, string.IsNullOrEmpty(instanceId)));
            }
            return result;
        }

        public GdDict GetComponentHolderIds()
        {
            if (!EnsureComponentOwner()) return new GdDict();
            return new GdDict { { "player", ComponentPlayerHolder },
                { "ship_cargo", _componentOpenHolder.StartsWith("ship_cargo:", StringComparison.Ordinal) && HolderAccess(_componentOpenHolder) == "ok" ? _componentOpenHolder : "" },
                { "cart", _componentOpenHolder.StartsWith("cart:", StringComparison.Ordinal) && HolderAccess(_componentOpenHolder) == "ok" ? _componentOpenHolder : "" } };
        }

        public void CloseComponentStorage() { if (!ComponentGenerationRestoreInProgress) _componentOpenHolder = ""; }

        string HolderAccess(string holderId)
        {
            if (holderId == ComponentPlayerHolder) return HasPlayer ? "ok" : "player_missing";
            GdDict holder = _componentDomain.GetSummary().GetDictOrEmpty("holders").GetDictOrEmpty(holderId);
            if (holder.IsEmpty) return "missing_holder";
            if (_componentOpenHolder != holderId) return "storage_closed";
            if (holder.GetString("kind") == "ship_cargo")
            {
                CargoHoldControl control = CargoHoldControls.FirstOrDefault(c => c.CarrierId == holder.GetString("owner_id") && c.IsValid && c.IsInsideTree && HasInteractionSightAndReach(c));
                return control == null ? "storage_out_of_range" : "ok";
            }
            if (holder.GetString("kind") == "cart")
            {
                ShipInstance ship = FindShipById(holder.GetString("owner_id"));
                CartState cart = ship?.GetCarts().FirstOrDefault(c => CartHolder(ship.ShipId, c.CartId) == holderId);
                CartControl control = cart == null ? null : CartControls.FirstOrDefault(c => c.CartId == cart.CartId && c.IsValid && c.IsInsideTree && HasInteractionSightAndReach(c));
                return control == null ? "storage_out_of_range" : "ok";
            }
            return "holder_inaccessible";
        }

        public GdDict RequestComponentRemoval(string instanceId, string destinationHolderId = null)
        {
            if (ComponentIntegrationEnabled && ComponentTerminalPending) return ComponentFailure("terminal_pending");
            if (!EnsureComponentOwner()) return ComponentFailure("component_integration_inactive");
            GdDict domain = CaptureComponentDomain(), row = Instances(domain).Get(instanceId ?? "") as GdDict;
            if (ComponentCaptureFailed(domain)) return domain;
            if (row == null) return ComponentFailure("missing_instance");
            GdDict source = domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder"));
            if (source.GetString("kind") != "slot") return ComponentFailure("not_installed");
            string destination = destinationHolderId ?? ComponentPlayerHolder;
            if (destination != ComponentPlayerHolder) return ComponentFailure("remove_to_player_required");
            return StartComponentWork(row, domain.GetDictOrEmpty("physical_slots").GetDictOrEmpty(row.GetString("holder")), destination, "dismount_component");
        }

        public GdDict RequestComponentInstall(string instanceId, string shipId, string slotId)
        {
            if (ComponentIntegrationEnabled && ComponentTerminalPending) return ComponentFailure("terminal_pending");
            if (!EnsureComponentOwner()) return ComponentFailure("component_integration_inactive");
            GdDict domain = CaptureComponentDomain(), row = Instances(domain).Get(instanceId ?? "") as GdDict;
            if (ComponentCaptureFailed(domain)) return domain;
            if (row == null) return ComponentFailure("missing_instance");
            if (row.GetString("holder") != ComponentPlayerHolder) return ComponentFailure("component_not_carried");
            GdDict slot = domain.GetDictOrEmpty("physical_slots").GetDictOrEmpty(SlotHolder(shipId, slotId));
            if (slot.IsEmpty) return ComponentFailure("missing_physical_slot");
            GdDict target = TargetRow(domain, slot, row, false);
            if (!target.GetBool("ok")) return ComponentFailure(target.GetString("reason"));
            return StartComponentWork(row, slot, slot.GetString("holder_id"), "mount_component");
        }

        string ComponentWorkGate(GdDict work, GdDict domain)
        {
            if (ComponentTerminalPending) return "terminal_pending";
            GdDict row = Instances(domain).Get(work.GetString("instance_id")) as GdDict;
            if (row == null || row.GetString("holder") != work.GetString("source_holder_id")) return "source_changed";
            if (row.GetInt("revision") != work.GetInt("expected_instance_revision")) return "stale_instance";
            GdDict source = domain.GetDictOrEmpty("holders").GetDictOrEmpty(work.GetString("source_holder_id"));
            GdDict destination = domain.GetDictOrEmpty("holders").GetDictOrEmpty(work.GetString("destination_holder_id"));
            if (source.GetInt("revision") != work.GetInt("expected_source_revision") || destination.GetInt("revision") != work.GetInt("expected_destination_revision")) return "stale_holder";
            GdDict slot = domain.GetDictOrEmpty("physical_slots").GetDictOrEmpty(work.GetString("physical_holder_id"));
            if (slot.IsEmpty || CurrentShip?.ShipId != slot.GetString("ship_id")) return "work_context_changed";
            Vec3 anchor = PhysicalWorld(slot);
            if (!HasPlayer || anchor == Vec3.Inf || PlayerPos.DistanceTo(anchor) > WORK_ACTION_INTERACT_RANGE) return "left_work_site";
            if (!ComponentSight(anchor)) return "line_of_sight";
            if (!ComponentWrench()) return "missing_tool";
            if ((PlayerProgression?.GetSkillLevel("salvage") ?? 0) < WorkActionDriver.Catalog.GetAction(work.GetString("action_id")).GetInt("min_skill_level")) return "insufficient_skill";
            if (work.GetString("action_id") == "mount_component")
            {
                GdDict target = TargetRow(domain, slot, row, false);
                if (!target.GetBool("ok")) return target.GetString("reason");
            }
            return "ok";
        }

        GdDict StartComponentWork(GdDict row, GdDict slot, string destination, string action)
        {
            if (ComponentTerminalPending) return ComponentFailure("terminal_pending");
            if (_componentMutating || _componentPublishing) return ComponentFailure("reentrant_mutation");
            if (ManualStudyRunning || WorkActionDriver?.IsWorking() == true) return ComponentFailure("work_busy");
            GdDict domain = CaptureComponentDomain();
            if (ComponentCaptureFailed(domain)) return domain;
            long sequence = domain.GetInt("command_sequence");
            if (sequence == long.MaxValue || domain.GetInt("revision") == long.MaxValue) return ComponentFailure("revision_overflow");
            var job = new GdDict { { "job_id", "component_work:" + RunId + ":" + (sequence + 1) },
                { "command_id", "live_component:" + RunId + ":" + (sequence + 1) }, { "status", "active" },
                { "action_id", action }, { "instance_id", row.GetString("instance_id") }, { "source_holder_id", row.GetString("holder") },
                { "destination_holder_id", destination }, { "physical_holder_id", slot.GetString("holder_id") },
                { "ship_id", slot.GetString("ship_id") }, { "slot_id", slot.GetString("slot_id") },
                { "expected_instance_revision", row.GetInt("revision") },
                { "expected_source_revision", domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetInt("revision") },
                { "expected_destination_revision", domain.GetDictOrEmpty("holders").GetDictOrEmpty(destination).GetInt("revision") },
                { "progress", 0.0 }, { "duration", WorkActionDriver.Catalog.GetAction(action).GetFloat("duration") },
                { "resume_required", false }, { "reason", "" } };
            string gate = ComponentWorkGate(job, domain);
            if (gate != "ok") return ComponentFailure(gate);
            if (VitalsState != null && VitalsState.Stamina <= 0.001) return ComponentFailure("exhausted");
            domain["component_work"] = job; domain["command_sequence"] = sequence + 1;
            domain["revision"] = checked(domain.GetInt("revision") + 1);
            if (!_componentDomain.ApplySummary(domain)) return ComponentFailure("invalid_component_work");
            MirrorComponentWork(job);
            _componentNoiseAcc = 0;
            WorkActionDriver.LastProgressNoise = 0; WorkActionDriver.LastNoisePulse = 0; WorkActionDriver.LastResolve = new GdDict();
            _workRequiresHold = HoldToWorkEnabled; _workAwaitingResume = false;
            CaptureWorkSite(); RefreshWorkActionHud();
            return new GdDict { { "ok", true }, { "committed", false }, { "reason", "started" }, { "job_id", job.Get("job_id") }, { "command_id", job.Get("command_id") } };
        }

        public GdDict GetComponentWorkState() => !EnsureComponentOwner() ? new GdDict() : _componentDomain.GetSummary().GetDictOrEmpty("component_work").DeepCopy();

        void MirrorComponentWork(GdDict job)
        {
            if (job.IsEmpty || WorkActionDriver == null) return;
            var work = new WorkActionState();
            work.ConfigureAction(job.GetString("action_id"), WorkActionDriver.Catalog.GetAction(job.GetString("action_id")));
            work.TargetId = job.GetString("instance_id"); work.Progress = job.GetFloat("progress"); work.Duration = job.GetFloat("duration");
            work.Status = job.GetString("status") == "active" ? WorkActionState.STATUS_ACTIVE : job.GetString("status") == "committed" ? WorkActionState.STATUS_IDLE : WorkActionState.STATUS_INTERRUPTED;
            work.BlockReason = job.GetString("reason"); WorkActionDriver.Work = work;
        }

        bool ResumeComponentWork()
        {
            if (ComponentGenerationRestoreInProgress) return false;
            if (!ComponentIntegrationEnabled || _componentDomain == null) return false;
            if (ComponentTerminalPending) return true;
            GdDict domain = CaptureComponentDomain(), job = domain.GetDictOrEmpty("component_work");
            if (ComponentCaptureFailed(domain) || domain.GetInt("revision") == long.MaxValue)
            { if (WorkActionDriver?.Work != null) WorkActionDriver.Work.BlockReason = "revision_overflow"; return true; }
            if (job.IsEmpty || !job.GetBool("resume_required")) return false;
            string gate = ComponentWorkGate(job, domain);
            if (gate != "ok") { WorkActionDriver.Work.BlockReason = gate; return true; }
            job["status"] = "active"; job["resume_required"] = false; job["reason"] = "";
            domain["revision"] = checked(domain.GetInt("revision") + 1);
            if (!_componentDomain.ApplySummary(domain)) return true;
            MirrorComponentWork(job); _workAwaitingResume = false; _workRequiresHold = HoldToWorkEnabled; CaptureWorkSite();
            return true;
        }

        void PauseComponentWork(string reason)
        {
            if (ComponentGenerationRestoreInProgress) return;
            if (_componentDomain == null || ComponentTerminalPending) return;
            GdDict domain = _componentDomain.GetSummary(), job = domain.GetDictOrEmpty("component_work");
            if (domain.GetInt("revision") == long.MaxValue) return;
            if (job.IsEmpty || job.GetString("status") == "committed") return;
            job["status"] = "interrupted"; job["resume_required"] = true; job["reason"] = reason;
            domain["revision"] = checked(domain.GetInt("revision") + 1);
            if (_componentDomain.ApplySummary(domain)) MirrorComponentWork(job);
            _workRequiresHold = false; _workAwaitingResume = true;
        }

        bool TickComponentWork(double delta)
        {
            if (ComponentGenerationRestoreInProgress) return true;
            if (!ComponentIntegrationEnabled || _componentDomain == null) return false;
            if (ComponentTerminalPending) return true;
            GdDict domain = CaptureComponentDomain(), job = domain.GetDictOrEmpty("component_work");
            if (ComponentCaptureFailed(domain) || domain.GetInt("revision") == long.MaxValue) return true;
            if (job.IsEmpty || job.GetString("status") != "active") return false;
            string gate = ComponentWorkGate(job, domain);
            if (gate != "ok") { PauseComponentWork(gate); RefreshWorkActionHud(); return true; }
            if (job.GetBool("resume_required") || delta <= 0 || (_workRequiresHold && HoldToWorkEnabled && !IsWorkInteractHeld)) return true;
            if (VitalsState != null && VitalsState.Stamina <= 0.001) { PauseComponentWork("exhausted"); return true; }
            double speed = WoundState?.WorkSpeedMultiplier() ?? 1;
            if (VitalsState != null)
            {
                speed *= GdMath.Clampf(0.35 + VitalsState.Stamina / Math.Max(1, VitalsState.MaxStamina) * 0.65, 0.35, 1);
                VitalsState.ApplyDelta(new GdDict { { "stamina", -8.0 * delta } });
            }
            job["progress"] = Math.Min(job.GetFloat("duration"), job.GetFloat("progress") + delta * speed);
            domain["revision"] = checked(domain.GetInt("revision") + 1);
            if (!_componentDomain.ApplySummary(domain)) return true;
            MirrorComponentWork(job);
            WorkActionDriver.LastProgressNoise = 0;
            GdDict actionDefinition = WorkActionDriver.Catalog.GetAction(job.GetString("action_id"));
            string verb = actionDefinition.GetString("verb"); double noise = actionDefinition.GetFloat("noise");
            if (job.GetFloat("progress") < job.GetFloat("duration") && (verb == "cut" || verb == "pry" || verb == "unbolt") && noise > 0.05)
            {
                _componentNoiseAcc += delta;
                if (_componentNoiseAcc >= WorkActionDriver.PROGRESS_NOISE_INTERVAL)
                {
                    _componentNoiseAcc = 0; WorkActionDriver.LastProgressNoise = noise * WorkActionDriver.PROGRESS_NOISE_FRACTION;
                    WorkActionDriver.LastNoisePulse = Math.Max(WorkActionDriver.LastNoisePulse, WorkActionDriver.LastProgressNoise);
                    if (ThreatManager != null) WorkActionDriver.ApplyNoiseToDetection(ThreatManager);
                    PlaySfx(AudioEventSeam.UI_WORK_PROGRESS);
                }
            }
            if (job.GetFloat("progress") >= job.GetFloat("duration")) CommitComponentWork();
            RefreshWorkActionHud(); return true;
        }

        GdDict TransferCommand(GdDict domain, string id, string destination, string commandId)
        {
            GdDict row = Instances(domain).GetDictOrEmpty(id), holders = domain.GetDictOrEmpty("holders");
            return new GdDict { { "schema_version", 1L }, { "command_id", commandId }, { "operation", "transfer" },
                { "instance_id", id }, { "source_holder_id", row.GetString("holder") }, { "destination_holder_id", destination },
                { "expected_domain_revision", domain.GetInt("revision") }, { "expected_instance_revision", row.GetInt("revision") },
                { "expected_source_revision", holders.GetDictOrEmpty(row.GetString("holder")).GetInt("revision") },
                { "expected_destination_revision", holders.GetDictOrEmpty(destination).GetInt("revision") } };
        }

        string ComponentCapacity(GdDict domain, string destination, GdDict row)
        {
            GdDict holder = domain.GetDictOrEmpty("holders").GetDictOrEmpty(destination);
            if (holder.IsEmpty) return "missing_holder";
            if (holder.GetString("kind") == "player") return "ok"; // Existing player weight remains a soft cap.
            double stackMass = 0;
            double capacity = holder.GetFloat("capacity_mass");
            ShipInstance ship = FindShipById(holder.GetString("owner_id"));
            if (holder.GetString("kind") == "ship_cargo" && ship != null)
            {
                stackMass = ship.GetInventory().GetTotalWeight() - ComponentMassFor(destination);
                capacity = ship.GetInventory().MaxWeight;
            }
            else if (holder.GetString("kind") == "cart" && ship != null)
            {
                CartState cart = ship.GetCarts().FirstOrDefault(c => CartHolder(ship.ShipId, c.CartId) == destination);
                if (cart == null) return "missing_holder";
                stackMass = cart.GetHold().GetTotalWeight() - ComponentMassFor(destination);
                capacity = cart.GetHold().MaxWeight;
            }
            if (holder.Has("capacity_mass") && stackMass + ComponentMassFor(destination) + row.GetFloat("mass") > capacity) return "capacity_mass";
            return "ok";
        }

        public GdDict RequestComponentTransfer(string instanceId, string destinationHolderId)
        {
            if (ComponentIntegrationEnabled && ComponentTerminalPending) return ComponentFailure("terminal_pending");
            if (!EnsureComponentOwner()) return ComponentFailure("component_integration_inactive");
            if (_componentMutating || _componentPublishing) return ComponentFailure("reentrant_mutation");
            GdDict domain = CaptureComponentDomain(), row = Instances(domain).Get(instanceId ?? "") as GdDict;
            if (ComponentCaptureFailed(domain)) return domain;
            if (domain.GetInt("revision") == long.MaxValue) return ComponentFailure("revision_overflow");
            if (row == null) return ComponentFailure("missing_instance");
            if (domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("kind") == "slot") return ComponentFailure("remove_work_required");
            if (domain.GetDictOrEmpty("holders").GetDictOrEmpty(destinationHolderId ?? "").GetString("kind") == "slot") return ComponentFailure("install_work_required");
            string gate = HolderAccess(row.GetString("holder"));
            if (gate != "ok") return ComponentFailure(gate);
            gate = HolderAccess(destinationHolderId ?? ""); if (gate != "ok") return ComponentFailure(gate);
            gate = ComponentCapacity(domain, destinationHolderId, row); if (gate != "ok") return ComponentFailure(gate);
            long sequence = domain.GetInt("command_sequence");
            if (sequence == long.MaxValue) return ComponentFailure("revision_overflow");
            string commandId = "live_component:" + RunId + ":" + (sequence + 1);
            return ExecuteComponentCommand(TransferCommand(domain, instanceId, destinationHolderId, commandId), candidate => {
                candidate["command_sequence"] = sequence + 1; return candidate;
            });
        }

        void CommitComponentWork()
        {
            if (ComponentTerminalPending) return;
            GdDict domain = CaptureComponentDomain(), job = domain.GetDictOrEmpty("component_work");
            if (ComponentCaptureFailed(domain)) return;
            string gate = ComponentWorkGate(job, domain);
            if (gate != "ok") { PauseComponentWork(gate); return; }
            GdDict command = TransferCommand(domain, job.GetString("instance_id"), job.GetString("destination_holder_id"), job.GetString("command_id"));
            command["xp_multipliers"] = PlayerProgression.XpMultipliers.DeepCopy();
            GdDict result = ExecuteComponentCommand(command, candidate => {
                candidate.GetDictOrEmpty("component_work")["status"] = "committed";
                candidate.GetDictOrEmpty("component_work")["resume_required"] = false;
                candidate.GetDictOrEmpty("component_work")["reason"] = "committed";
                StageComponentTraining(candidate, job); return candidate;
            });
            if (!result.GetBool("committed")) { PauseComponentWork(result.GetString("reason")); return; }
            _workRequiresHold = false; _workAwaitingResume = false;
            MirrorComponentWork(_componentDomain.GetSummary().GetDictOrEmpty("component_work"));
            GdDict definition = WorkActionDriver.Catalog.GetAction(job.GetString("action_id"));
            WorkActionDriver.LastNoisePulse = definition.GetFloat("noise");
            WorkActionDriver.LastXpEvent = definition.GetString("xp_event");
            WorkActionDriver.LastResolve = new GdDict { { "ok", true }, { "verb", definition.GetString("verb") },
                { "audio_event", AudioEventSeam.SfxForWorkVerb(definition.GetString("verb")) }, { "commit_id", result.Get("commit_id") } };
            if (WorkActionDriver.LastNoisePulse > 0 && ThreatManager != null) WorkActionDriver.ApplyNoiseToDetection(ThreatManager);
            if (AudioManager != null) WorkActionDriver.EmitCompletionSfx(AudioManager.SfxRouter);
        }

        void StageComponentTraining(GdDict candidate, GdDict job)
        {
            string eventId = WorkActionDriver.Catalog.GetAction(job.GetString("action_id")).GetString("xp_event");
            GdDict participants = candidate.GetDictOrEmpty("participating_state");
            var progression = new PlayerProgressionState();
            var classes = ClassDefinition.LoadAll(); classes.TryGetValue(PlayerProgression.ClassId, out ClassDefinition definition);
            progression.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgression.GetBooksCatalog());
            if (PaidCraftingState.IsDomainVersion(candidate.GetInt("schema_version")))
            {
                if (!PaidCraftRewardProof.CopyProgressionExact(progression, participants.GetDictOrEmpty("progression"))) throw new ArgumentException("invalid_paid_progression");
            }
            else progression.ApplySummary(participants.GetDictOrEmpty("progression"));
            progression.XpMultipliers.Clear(); foreach (var entry in PlayerProgression.XpMultipliers) progression.XpMultipliers[entry.Key] = entry.Value;
            var emitter = new TrainingEventBus(); emitter.Configure(); emitter.ApplySummary(participants.GetDictOrEmpty("training"));
            emitter.SkillGate = TrainingEventBus.SkillGate; emitter.EventFilter = TrainingEventBus.EventFilter;
            GdDict record = emitter.Emit(eventId, job.GetString("instance_id"), progression);
            var recorded = new TrainingEventBus(); recorded.Configure(); recorded.ApplySummary(participants.GetDictOrEmpty("training"));
            if (record != null) recorded.RecordApplied(record, "component_transfer:" + job.GetString("command_id"));
            GdDict training = recorded.ToDict(); training["xp_total"] = emitter.GetTotalXpDelivered(); training["dropped"] = emitter.GetDroppedCount();
            participants["progression"] = progression.GetSummary(); participants["training"] = training;
            if (PaidCraftingEnabled) PaidCraftRewardProof.RefreshCurrent(participants.GetDictOrEmpty("paid_crafting"), training);
        }

        GdDict ExecuteComponentCommand(GdDict command, Func<GdDict, GdDict> effects)
        {
            if (ComponentGenerationRestoreInProgress) return ComponentFailure("restore_in_progress");
            if (ComponentTerminalPending) return ComponentFailure("terminal_pending");
            if (_componentMutating || _componentPublishing) return ComponentFailure("reentrant_mutation");
            _componentMutating = true;
            try
            {
                GdDict preparation = _componentDomain.PrepareLive(command, candidate => {
                    GdDict staged = effects(candidate);
                    double oldMass = ComponentMassFor(ComponentPlayerHolder);
                    double newMass = Instances(staged).Values.OfType<GdDict>().Where(row => row.GetString("holder") == ComponentPlayerHolder).Sum(row => row.GetFloat("mass"));
                    staged.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory")["total_weight"] =
                        staged.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory").GetFloat("total_weight") - oldMass + newMass;
                    return staged;
                });
                if (!preparation.GetBool("ok")) return preparation;
                return _componentDomain.Commit(preparation.GetString("transaction_id"));
            }
            catch (Exception error) { GdDict failed = ComponentFailure("component_staging_failed"); failed["detail"] = error.GetType().Name; return failed; }
            finally { _componentMutating = false; }
        }

        void ApplyComponentViews(GdDict candidate)
        {
            GdDict before = _componentDomain?.GetSummary();
            GdDict beforeParticipants = ReadComponentParticipants();
            // Canonical capture intentionally ignores mutable legacy projections; rollback must retain their actual before-images.
            GdDict rawCrafting = PaidCraftingEnabled ? CraftingState.GetSummary().DeepCopy() : null;
            GdDict rawField = PaidCraftingEnabled ? FieldCraftingState.GetSummary().DeepCopy() : null;
            GdDict rawKnowledge = RecipeKnowledge?.GetSummary().DeepCopy();
            GdDict beforeMultipliers = PlayerProgression?.XpMultipliers.DeepCopy() ?? new GdDict();
            GdDict nextMultipliers = beforeMultipliers.DeepCopy();
            string nextClass = candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression").GetString("class_id");
            if (PlayerProgression != null && nextClass != PlayerProgression.ClassId)
            {
                var classes = ClassDefinition.LoadAll();
                if (!classes.TryGetValue(nextClass, out ClassDefinition definition)) throw new InvalidOperationException("invalid_component_class");
                nextMultipliers = definition.XpMultipliers.DeepCopy();
            }
            var beforeMachines = new Dictionary<ShipSubcomponent, double>();
            var machineTargets = new Dictionary<ShipSubcomponent, double>();
            foreach (GdDict machine in candidate.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
            {
                ShipSubcomponent sub = FindShipById(machine.GetString("owner_id"))?.SystemsManager?.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id"));
                if (sub == null) throw new InvalidOperationException("component_machinery_owner_missing");
                beforeMachines[sub] = sub.Health; machineTargets[sub] = machine.GetFloat("health");
            }
            _componentPublishing = true;
            bool writing = false;
            try
            {
                // Retained fault labels run while every published view still belongs to the old owner.
                ComponentStageHook?.Invoke("live_inventory");
                ComponentStageHook?.Invoke("live_machinery");
                ComponentStageHook?.Invoke("live_placement");
                if (_paidRestoreOperation != null) ValidateFinalPaidRestore(candidate);
                else if (_componentMutating && _paidPublicationContext != null) ValidateFinalPaidPublication(before);
                else if (_componentMutating) ValidateFinalComponentPublication(before, candidate);
                else if (!V.VariantEquals(beforeParticipants, ReadComponentParticipants())) throw new InvalidOperationException("stale_context");
                foreach (var pair in beforeMachines) if (pair.Key.Health != pair.Value) throw new InvalidOperationException("stale_context");
                // No hooks, notifications, gate/filter or resource calls between these assignments and coordinator publication.
                writing = true;
                ApplyComponentParticipants(candidate.GetDictOrEmpty("participating_state"), nextMultipliers);
                foreach (var pair in machineTargets) pair.Key.Health = pair.Value;
                ProjectComponentPlacement(candidate);
            }
            catch
            {
                if (writing)
                {
                    ApplyComponentParticipants(beforeParticipants, beforeMultipliers);
                    if (PaidCraftingEnabled)
                    {
                        CraftingState.ApplyOwnedSummary(rawCrafting); FieldCraftingState.ApplyOwnedSummary(rawField);
                        if (rawKnowledge != null) RecipeKnowledge.ApplySummary(rawKnowledge);
                    }
                    foreach (var item in beforeMachines) item.Key.Health = item.Value;
                    if (before != null) ProjectComponentPlacement(before);
                }
                throw;
            }
            finally { _componentPublishing = false; }
        }

        void ValidateFinalComponentPublication(GdDict before, GdDict candidate)
        {
            if (ComponentTerminalPending) throw new InvalidOperationException("terminal_pending");
            if (before == null || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true) throw new InvalidOperationException("stale_context");
            GdDict receipt = candidate.GetDictOrEmpty("receipts").Values.OfType<GdDict>()
                .Single(row => !before.GetDictOrEmpty("receipts").Has(row.GetString("transaction_id")));
            GdDict effect = receipt.GetDictOrEmpty("result");
            string source = effect.GetString("source_holder_id"), destination = effect.GetString("destination_holder_id");
            Vec3 position = PlayerPos;
            string gate;
            if (before.GetDictOrEmpty("holders").GetDictOrEmpty(source).GetString("kind") == "slot" ||
                before.GetDictOrEmpty("holders").GetDictOrEmpty(destination).GetString("kind") == "slot")
            {
                GdDict work = before.GetDictOrEmpty("component_work");
                gate = work.GetString("command_id") == receipt.GetString("command_id") ? ComponentWorkGate(work, before) : "stale_context";
                if (_workRequiresHold && HoldToWorkEnabled && !IsWorkInteractHeld) gate = "stale_context";
            }
            else
            {
                gate = HolderAccess(source);
                if (gate == "ok") gate = HolderAccess(destination);
                if (gate == "ok") gate = ComponentCapacity(before, destination, Instances(before).GetDictOrEmpty(effect.GetString("instance_id")));
            }
            if (ComponentTerminalPending) throw new InvalidOperationException("terminal_pending");
            if (gate != "ok" || position != PlayerPos || !V.VariantEquals(before.Get("participating_state"), ReadComponentParticipants()))
                throw new InvalidOperationException("stale_context");
            foreach (GdDict machine in before.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
            {
                ShipSubcomponent sub = FindShipById(machine.GetString("owner_id"))?.SystemsManager?.GetSystem(machine.GetString("system_id"))?.GetSubcomponent(machine.GetString("subcomponent_id"));
                if (sub == null || sub.Health != machine.GetFloat("health")) throw new InvalidOperationException("stale_context");
            }
        }

        void ApplyComponentParticipants(GdDict participants, GdDict multipliers)
        {
            InventoryState?.ApplySummary(participants.GetDictOrEmpty("inventory"));
            if (PlayerProgression != null)
            {
                GdDict progression = participants.GetDictOrEmpty("progression");
                PlayerProgression.ClassId = progression.GetString("class_id");
                PlayerProgression.Skills = progression.GetDictOrEmpty("skills").DeepCopy();
                PlayerProgression.SkillXp = progression.GetDictOrEmpty("skill_xp").DeepCopy();
                PlayerProgression.SkillXpFractional = progression.GetDictOrEmpty("skill_xp_fractional").DeepCopy();
                PlayerProgression.CrossTraining = progression.GetDictOrEmpty("cross_training").DeepCopy();
                PlayerProgression.BooksRead = progression.GetDictOrEmpty("books_read").DeepCopy();
                PlayerProgression.XpMultipliers.Clear(); foreach (var entry in multipliers) PlayerProgression.XpMultipliers[entry.Key] = entry.Value;
            }
            if (TrainingEventBus != null) { TrainingEventBus.Reset(); if (!TrainingEventBus.ApplySummary(participants.GetDictOrEmpty("training"))) throw new InvalidOperationException("invalid_component_training"); }
            if (PaidCraftingEnabled && participants.Has("paid_crafting")) ApplyPaidProjections(participants);
            foreach (var pair in participants.GetDictOrEmpty("stacks"))
            {
                string holderId = V.Str(pair.Key);
                foreach (ShipInstance ship in AllKnownShips())
                {
                    if (CargoHolder(ship.ShipId) == holderId && pair.Value is GdDict cargo) ship.GetInventory().ApplySummary(cargo);
                    foreach (CartState cart in ship.GetCarts()) if (CartHolder(ship.ShipId, cart.CartId) == holderId && pair.Value is GdDict hold) cart.GetHold().ApplySummary(hold);
                }
            }
        }

        void ProjectComponentPlacement(GdDict domain)
        {
            if (!ComponentIntegrationEnabled) return;
            foreach (ShipInstance ship in AllKnownShips())
            {
                if (!domain.GetArrayOrEmpty("registered_owners").Contains(ship.ShipId)) continue;
                var placed = new GdArray();
                foreach (GdDict slot in domain.GetDictOrEmpty("physical_slots").Values.OfType<GdDict>())
                {
                    if (slot.GetString("ship_id") != ship.ShipId) continue;
                    GdDict row = Instances(domain).Values.OfType<GdDict>().FirstOrDefault(instance => instance.GetString("holder") == slot.GetString("holder_id"));
                    GdDict entry = slot.DeepCopy();
                    entry["mounted"] = row != null;
                    entry["component_instance_id"] = row?.GetString("instance_id") ?? "";
                    entry["component_id"] = row?.GetString("definition_id") ?? "";
                    entry["item_form"] = row?.GetString("item_form") ?? "";
                    entry["condition"] = row?.Get("condition"); entry["mass"] = row?.Get("mass") ?? 0.0;
                    GdDict machine = domain.GetDictOrEmpty("machinery").GetDictOrEmpty(domain.GetDictOrEmpty("holders").GetDictOrEmpty(slot.GetString("holder_id")).GetString("machinery_id"));
                    entry["linked_system"] = machine.GetString("system_id"); entry["linked_subcomponent"] = machine.GetString("subcomponent_id");
                    placed.Add(entry);
                }
                var projection = new GdDict { { "schema", "component_placement_v1" }, { "seed", ship.ComponentPlacementSummary.GetInt("seed") },
                    { "count", (long)placed.Count }, { "placed", placed } };
                ship.ComponentPlacementSummary = projection;
                if (ship == CurrentShip)
                {
                    if (ComponentPlacementState == null) ComponentPlacementState = new ComponentPlacementState();
                    ComponentPlacementState.ApplySummary(projection);
                    ComponentPlacementState.RequireInstanceOwner = true;
                }
            }
        }

        void NotifyComponentPublication(GdDict result)
        {
            BindComponentReadViews();
            if (PaidCraftingState.IsOperation(result.GetDictOrEmpty("result").GetString("operation")))
            {
                // Core effects, including spoilage, are already applied before any fallible presentation callback.
                NotifyPaidCraft(result);
                ComponentDomainChanged?.Invoke(result.DeepCopy());
                return;
            }
            ComponentDomainChanged?.Invoke(result.DeepCopy());
            RebuildComponentMarkers(); RefreshStationTiersFromShipMod(); RecomputePlayerEncumbrance(); RefreshInventoryHud();
        }
    }
}
