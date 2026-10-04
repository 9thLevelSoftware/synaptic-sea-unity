using System;
using System.Globalization;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Explicit capability-gated complete-world capture.</summary>
    public static class SavePayloadAssembler
    {
        static GdDict PaidCraftingSummary(GdDict summary, GdDict participants)
        {
            GdDict detached = summary.DeepCopy();
            void CompleteConsent(GdDict mirror, GdDict canonical)
            {
                GdDict ownedStations = canonical.GetDictOrEmpty("station_summaries");
                foreach (var pair in mirror.GetDictOrEmpty("station_summaries"))
                {
                    if (!(pair.Value is GdDict station) || station.Has("resume_required") ||
                        !(ownedStations.Get(pair.Key) is GdDict owned) || !owned.Has("resume_required")) continue;
                    if (!(owned.Get("resume_required") is bool consent))
                        throw new PaidSnapshotCodec.ValidationException("crafting_summary.station_summaries.resume_required:invalid_canonical_boolean");
                    station["resume_required"] = consent;
                }
            }
            CompleteConsent(detached, participants.GetDictOrEmpty("crafting"));
            CompleteConsent(detached.GetDictOrEmpty("field_crafting"), participants.GetDictOrEmpty("field_crafting").GetDictOrEmpty("field_crafting"));
            return detached;
        }

        static GdDict PaidPlacementSummary(GdDict summary)
        {
            GdDict detached = summary.DeepCopy();
            foreach (object value in detached.GetArrayOrEmpty("placed"))
            {
                if (!(value is GdDict row) || !row.Has("local_position")) continue;
                object local = row.Get("local_position");
                if (local is Vec3 vector)
                {
                    if (double.IsNaN(vector.X) || double.IsInfinity(vector.X) || double.IsNaN(vector.Y) || double.IsInfinity(vector.Y) ||
                        double.IsNaN(vector.Z) || double.IsInfinity(vector.Z))
                        throw new PaidSnapshotCodec.ValidationException("component_placement.placed.local_position:Vec3:nonfinite");
                    row["local_position"] = GdArray.Of((double)vector.X, (double)vector.Y, (double)vector.Z);
                }
                else if (!(local is GdArray array) || array.Count != 3 ||
                    array.Any(axis => !(axis is double number) || double.IsNaN(number) || double.IsInfinity(number)))
                    throw new PaidSnapshotCodec.ValidationException("component_placement.placed.local_position:" + (local?.GetType().FullName ?? "null") + ":invalid_position");
            }
            return detached;
        }

        static GdDict PaidRetainedPlacements(GdDict ships)
        {
            GdDict detached = ships.DeepCopy();
            foreach (GdDict ship in detached.Values.OfType<GdDict>())
                if (ship.Get("component_placement") is GdDict placement)
                    ship["component_placement"] = PaidPlacementSummary(placement);
            return detached;
        }

        // ObjectiveProgressState documents positive Int64 sequence keys whose JSON wire form is decimal text.
        // Adapt only this detached DTO field; arbitrary Variant-key dictionaries remain invalid paid JSON.
        static GdDict PaidObjectiveSummary(GdDict summary)
        {
            var wire = new GdDict();
            foreach (var pair in summary)
            {
                string key;
                if (pair.Key is long sequence && sequence > 0)
                    key = sequence.ToString(CultureInfo.InvariantCulture);
                else if (pair.Key is string text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) &&
                    parsed > 0 && text == parsed.ToString(CultureInfo.InvariantCulture)) key = text;
                else throw new PaidSnapshotCodec.ValidationException("$[\"objective_progress_summary\"][<key>]:" +
                    (pair.Key?.GetType().FullName ?? "null") + ":invalid_objective_sequence");
                if (wire.Has(key)) throw new PaidSnapshotCodec.ValidationException("$[\"objective_progress_summary\"][\"" + key + "\"]:System.String:objective_sequence_collision");
                wire[key] = V.DeepCopy(pair.Value);
            }
            return wire;
        }

        public static bool HasUnverifiedCraft(object value)
        {
            if (value is GdDict d)
            {
                if (d.Has("active_craft") && (!(d.Get("active_craft") is GdDict active) || !active.IsEmpty)) return true;
                if (d.Has("active_recipe_id") && (!(d.Get("active_recipe_id") is string id) || id.Length > 0)) return true;
                if (d.Has("queue") && (!(d.Get("queue") is GdArray queue) || queue.Count > 0)) return true;
                foreach (object child in d.Values) if (HasUnverifiedCraft(child)) return true;
            }
            else if (value is GdArray a) foreach (object child in a) if (HasUnverifiedCraft(child)) return true;
            return false;
        }
        public static GdDict Build(RunSession session, string slotId, string slotKind)
        {
            GdDict Fail(string why) => new GdDict { { "ok", false }, { "reason", why }, { "payloads", null } };
            if (session == null || !session.CompleteGenerationEnabled) return Fail("component_integration_not_enabled");
            if (session.ComponentGenerationRestoreInProgress) return Fail("restore_in_progress");
            if (session.ComponentTerminalPending) return Fail("terminal_pending");
            if (!session.PlayableStarted || session.SliceComplete || session.SaveLoadService == null) return Fail("run_not_playable");
            if (session.DomainPublicationInProgress) return Fail("component_publication_in_progress");
            if (slotKind != SaveLoadService.ComponentSlotKind(slotId)) return Fail("invalid_slot_kind");
            if (!session.PaidCraftingEnabled && (HasUnverifiedCraft(session.CraftingState?.GetSummary()) || HasUnverifiedCraft(session.FieldCraftingState?.GetSummary()))) return Fail("craft_payment_unverified");
            try
            {
                session.SyncCombatSummaryForSave(); session.SyncArcSummaryForSave(); session.SyncBreachEnvironmentForSave(); session.SyncPillarSummariesForSave();
                GdDict domain = session.PaidCraftingEnabled ? session.CapturePaidCraftingDomain() : session.CaptureComponentDomain();
                if (!session.PaidCraftingEnabled && HasUnverifiedCraft(domain)) return Fail("craft_payment_unverified");
                string reason;
                if (!(session.PaidCraftingEnabled ? session.ValidatePaidCraftingRestore(domain, out reason) : session.ValidateComponentDomainRestore(domain, out reason))) return Fail(reason);
                foreach (object value in domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values)
                    if (!(value is GdDict row) || row.GetString("condition_state") != "known") return Fail("legacy_condition_unresolved");
                GdDict encoded = ComponentDomainCodec.Encode(domain);
                RunSnapshot active = RunSnapshotAssembler.Build(session); WorldSnapshot world = WorldSnapshotAssembler.Build(session);
                if (active == null || world == null || session.HomeShip == null || session.LifeboatShip == null) return Fail("complete_world_unavailable");
                if (session.PaidCraftingEnabled) active.ObjectiveProgressSummary = PaidObjectiveSummary(active.ObjectiveProgressSummary);
                GdDict prior = session.SaveLoadService.SelectGeneration(slotId);
                string parent = prior.GetBool("ok") && prior.GetString("run_id") == session.RunIdInternal ? prior.GetString("generation_id") : "";
                string pointer = parent.Length > 0 ? prior.GetString("selected_pointer_sha256") : "";
                long priorCapture = parent.Length > 0 ? prior.GetDictOrEmpty("payloads").GetInt("domain_revision") : 0L;
                if (prior.GetString("reason") == "slot_deleted")
                {
                    GdDict retained = session.SaveLoadService.ReadComponentCommitParent(session.RunIdInternal, slotId);
                    if (retained.GetBool("ok")) { parent = retained.GetString("parent_generation_id"); pointer = retained.GetString("expected_pointer_sha256"); priorCapture = retained.GetInt("domain_revision"); }
                }
                long capture = session.NextCaptureRevision(priorCapture);
                string generation = Guid.NewGuid().ToString("N"), captureText = capture.ToString(CultureInfo.InvariantCulture);
                if (!session.BuildGenerationDocumentSet(world, out GdDict references, out GdArray artifacts, out reason)) return Fail(reason);
                GdDict home = world.HomeShip.DeepCopy(), homeRef = references.GetDictOrEmpty("ship_start");
                if (session.PaidCraftingEnabled) home["objective_progress_summary"] = PaidObjectiveSummary(home.GetDictOrEmpty("objective_progress_summary"));
                string owner = world.CurrentLocation.Length == 0 ? "ship_start" : world.VisitedShips.GetDictOrEmpty(world.CurrentLocation).GetString("ship_id");
                if (owner.Length == 0 || !references.Has(owner)) return Fail("current_owner_missing");
                string poseOwner = session.CurrentOccupancy?.ShipId ?? owner;
                ShipInstance poseShip = session.FindShipByIdInternal(poseOwner);
                if (poseShip?.SceneRoot == null || !references.Has(poseOwner)) return Fail("player_pose_owner_missing");
                if (session.Scene != null && session.Scene.HasPlayer)
                {
                    Vec3 local = SessionMath.AffineInverse(poseShip.SceneRoot.GlobalTransform) * session.Scene.PlayerPosition;
                    world.PlayerPositionInShip = GdArray.Of((double)local.X, (double)local.Y, (double)local.Z);
                }
                world.AboardShipId = poseOwner;
                GdDict activeRef = references.GetDictOrEmpty(owner);
                active.LayoutPath = activeRef.GetString("layout_path"); active.GameplaySlicePath = activeRef.GetString("gameplay_slice_path"); active.KitPath = activeRef.GetString("kit_path");
                active.CurrentLocation = world.CurrentLocation; active.VisitedShips = world.VisitedShips.DeepCopy(); active.PlayerPosition = world.PlayerPositionInShip.DeepCopy();
                active.RunId = session.RunIdInternal; active.SlotId = slotId; active.SlotKind = slotKind; active.IsAutosave = slotKind == "auto"; active.IsQuicksave = slotKind == "quick";
                if (session.ComponentIntegrationEnabled) { active.SliceVersion = RunSnapshot.ComponentIntegrationVersion; active.GenerationId = generation; active.CaptureRevision = captureText; active.ComponentDomain = encoded.DeepCopy(); }
                if (session.PaidCraftingEnabled)
                {
                    active.CraftingSummary = PaidCraftingSummary(active.CraftingSummary, domain.GetDictOrEmpty("participating_state"));
                    active.CraftingSummary["paid_craft"] = PaidSnapshotCodec.Envelope(domain, session.ComponentIntegrationEnabled);
                }
                active.SavedAtEpoch = (long)session.Clock.UnixTime();
                if (session.CurrentShip?.SystemsManager != null && session.AwayFromStart) active.ShipSystemsSummary = session.CurrentShip.SystemsManager.GetSummary();
                home["layout_path"] = homeRef.Get("layout_path"); home["gameplay_slice_path"] = homeRef.Get("gameplay_slice_path"); home["kit_path"] = homeRef.Get("kit_path");
                home["current_location"] = ""; home["run_id"] = session.RunIdInternal;
                if (world.CurrentLocation.Length == 0) home["player_position"] = world.PlayerPositionInShip.DeepCopy();
                if (session.ComponentIntegrationEnabled) { home["slice_version"] = RunSnapshot.ComponentIntegrationVersion; home["generation_id"] = generation; home["capture_revision"] = captureText; home["component_domain"] = encoded.DeepCopy(); }
                if (session.PaidCraftingEnabled)
                {
                    GdDict crafting = PaidCraftingSummary(home.GetDictOrEmpty("crafting_summary"), domain.GetDictOrEmpty("participating_state"));
                    crafting["paid_craft"] = PaidSnapshotCodec.Envelope(domain, session.ComponentIntegrationEnabled);
                    home["crafting_summary"] = crafting;
                }
                if (session.HomeShip.SystemsManager != null)
                { GdDict systems = home.GetDictOrEmpty("ship_systems_summary"); foreach (var pair in session.HomeShip.SystemsManager.GetSummary()) systems[pair.Key] = pair.Value; home["ship_systems_summary"] = systems; }
                if (!session.HomeShip.ComponentPlacementSummary.IsEmpty) home["component_placement_summary"] = session.HomeShip.ComponentPlacementSummary.DeepCopy();
                if (session.PaidCraftingEnabled)
                {
                    active.ComponentPlacementSummary = PaidPlacementSummary(active.ComponentPlacementSummary);
                    home["component_placement_summary"] = PaidPlacementSummary(home.GetDictOrEmpty("component_placement_summary"));
                    home["visited_ships"] = PaidRetainedPlacements(home.GetDictOrEmpty("visited_ships"));
                    world.VisitedShips = PaidRetainedPlacements(world.VisitedShips);
                    active.VisitedShips = world.VisitedShips.DeepCopy();
                    GdDict lifeboat = world.MobileHomeState.GetDictOrEmpty("lifeboat");
                    if (lifeboat.Get("component_placement") is GdDict placement)
                        lifeboat["component_placement"] = PaidPlacementSummary(placement);
                }
                world.HomeShip = home; world.RunId = session.RunIdInternal;
                if (session.ComponentIntegrationEnabled) { world.SliceVersion = WorldSnapshot.ComponentIntegrationVersion; world.GenerationId = generation; world.CaptureRevision = captureText; world.ComponentDomain = encoded.DeepCopy(); }
                if (session.PaidCraftingEnabled && (!session.ValidatePaidCraftingRestore(domain, out reason) || !PaidSnapshotCodec.Same(domain, session.CapturePaidCraftingDomain()))) return Fail("capture_owner_changed");
                var revisions = new GdDict(); foreach (object id in references.Keys) revisions[id] = capture;
                var payloads = new GdDict
                {
                    { "schema_version", SaveCommitCoordinator.PayloadVersion }, { "generation_id", generation }, { "parent_generation_id", parent }, { "expected_pointer_sha256", pointer },
                    { "run_id", session.RunIdInternal }, { "slot_id", slotId }, { "slot_kind", slotKind }, { "domain_revision", capture }, { "compatibility", session.SaveLoadService.ComponentCompatibility() },
                    { "binding", new GdDict { { "binding_version", "component-generation-binding-1" }, { "component_revision", domain.GetInt("revision").ToString(CultureInfo.InvariantCulture) },
                        { "home_ship_id", "ship_start" }, { "lifeboat_ship_id", "lifeboat" }, { "current_owner_id", owner }, { "current_location", world.CurrentLocation }, { "player_pose_owner_id", poseOwner }, { "player_local_pose", session.PaidCraftingEnabled ? world.PlayerPositionInShip.DeepCopy() : GdJson.ParseString(GdJson.Stringify(world.PlayerPositionInShip)) },
                        { "owner_revisions", revisions }, { "ship_references", references } } },
                    { "run_text", session.PaidCraftingEnabled ? PaidSnapshotCodec.Stringify(active.ToDict(), PaidSnapshotCodec.SnapshotPolicy(session.ComponentIntegrationEnabled, false)) : GdJson.Stringify(active.ToDict(), "  ") }, { "world_text", session.PaidCraftingEnabled ? PaidSnapshotCodec.Stringify(world.ToDict(), PaidSnapshotCodec.SnapshotPolicy(session.ComponentIntegrationEnabled, true)) : GdJson.Stringify(world.ToDict(), "  ") }, { "artifacts", artifacts }
                };
                if (session.PaidCraftingEnabled) payloads.GetDictOrEmpty("binding")["save_mode"] = session.ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode;
                return new GdDict { { "ok", true }, { "reason", "captured" }, { "payloads", payloads } };
            }
            catch (Exception e) { return new GdDict { { "ok", false }, { "reason", "capture_failed" }, { "detail", e is PaidSnapshotCodec.ValidationException invalid ? invalid.Diagnostic : e.GetType().Name }, { "payloads", null } }; }
        }

        // Private rollback capture is not a save: no owner refresh, revision allocation, pointer read or live Sync.
        internal static WorldSnapshot CaptureBeforeWorld(RunSession session, RunSession.PaidRestoreOperation operation, out GdDict documents)
        {
            session.RequirePaidRestoreOperation(operation);
            WorldSnapshot world = WorldSnapshotAssembler.BuildDetached(session, operation);
            if (world == null || !session.BuildDetachedGenerationDocumentSet(world, operation, out GdDict references, out GdArray artifacts, out string reason))
                throw new InvalidOperationException("before_world_unavailable");
            GdDict home = world.HomeShip.DeepCopy(), homeRef = references.GetDictOrEmpty("ship_start");
            home["layout_path"] = homeRef.Get("layout_path"); home["kit_path"] = homeRef.Get("kit_path");
            home["gameplay_slice_path"] = homeRef.Get("gameplay_slice_path");
            home["slice_version"] = session.ComponentIntegrationEnabled ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION;
            world.HomeShip = home;
            world.SliceVersion = session.ComponentIntegrationEnabled ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion;
            ShipInstance aboard = session.FindShipByIdInternal(world.AboardShipId);
            if (aboard?.SceneRoot == null) throw new InvalidOperationException("before_pose_owner_missing");
            Vec3 local = SessionMath.AffineInverse(aboard.SceneRoot.GlobalTransform) * session.Scene.PlayerPosition;
            world.PlayerPositionInShip = GdArray.Of((double)local.X, (double)local.Y, (double)local.Z);
            documents = new GdDict { { "binding", new GdDict { { "ship_references", references } } }, { "artifacts", artifacts } };
            return world;
        }
    }
}
