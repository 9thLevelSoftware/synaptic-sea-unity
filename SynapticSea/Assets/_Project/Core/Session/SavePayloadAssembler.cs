using System;
using System.Globalization;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Explicit diagnostic complete-world capture. Ordinary saves continue to use their legacy adapters.</summary>
    public static class SavePayloadAssembler
    {
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
            if (session == null || !session.ComponentIntegrationEnabled) return Fail("component_integration_not_enabled");
            if (session.ComponentTerminalPending) return Fail("terminal_pending");
            if (!session.PlayableStarted || session.SliceComplete || session.SaveLoadService == null) return Fail("run_not_playable");
            if (session.ComponentPublicationInProgress) return Fail("component_publication_in_progress");
            if (slotKind != SaveLoadService.ComponentSlotKind(slotId)) return Fail("invalid_slot_kind");
            if (HasUnverifiedCraft(session.CraftingState?.GetSummary()) || HasUnverifiedCraft(session.FieldCraftingState?.GetSummary())) return Fail("craft_payment_unverified");
            try
            {
                session.SyncCombatSummaryForSave(); session.SyncArcSummaryForSave(); session.SyncBreachEnvironmentForSave(); session.SyncPillarSummariesForSave();
                GdDict domain = session.CaptureComponentDomain();
                if (HasUnverifiedCraft(domain)) return Fail("craft_payment_unverified");
                if (!session.ValidateComponentDomainRestore(domain, out string reason)) return Fail(reason);
                foreach (object value in domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values)
                    if (!(value is GdDict row) || row.GetString("condition_state") != "known") return Fail("legacy_condition_unresolved");
                GdDict encoded = ComponentDomainCodec.Encode(domain);
                RunSnapshot active = RunSnapshotAssembler.Build(session); WorldSnapshot world = WorldSnapshotAssembler.Build(session);
                if (active == null || world == null || session.HomeShip == null || session.LifeboatShip == null) return Fail("complete_world_unavailable");
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
                active.SliceVersion = RunSnapshot.ComponentIntegrationVersion; active.GenerationId = generation; active.CaptureRevision = captureText; active.ComponentDomain = encoded.DeepCopy();
                active.SavedAtEpoch = (long)session.Clock.UnixTime();
                if (session.CurrentShip?.SystemsManager != null && session.AwayFromStart) active.ShipSystemsSummary = session.CurrentShip.SystemsManager.GetSummary();
                home["layout_path"] = homeRef.Get("layout_path"); home["gameplay_slice_path"] = homeRef.Get("gameplay_slice_path"); home["kit_path"] = homeRef.Get("kit_path");
                home["current_location"] = ""; home["run_id"] = session.RunIdInternal;
                if (world.CurrentLocation.Length == 0) home["player_position"] = world.PlayerPositionInShip.DeepCopy();
                home["slice_version"] = RunSnapshot.ComponentIntegrationVersion; home["generation_id"] = generation; home["capture_revision"] = captureText; home["component_domain"] = encoded.DeepCopy();
                if (session.HomeShip.SystemsManager != null)
                { GdDict systems = home.GetDictOrEmpty("ship_systems_summary"); foreach (var pair in session.HomeShip.SystemsManager.GetSummary()) systems[pair.Key] = pair.Value; home["ship_systems_summary"] = systems; }
                if (!session.HomeShip.ComponentPlacementSummary.IsEmpty) home["component_placement_summary"] = session.HomeShip.ComponentPlacementSummary.DeepCopy();
                world.HomeShip = home; world.RunId = session.RunIdInternal; world.SliceVersion = WorldSnapshot.ComponentIntegrationVersion; world.GenerationId = generation; world.CaptureRevision = captureText; world.ComponentDomain = encoded.DeepCopy();
                var revisions = new GdDict(); foreach (object id in references.Keys) revisions[id] = capture;
                var payloads = new GdDict
                {
                    { "schema_version", SaveCommitCoordinator.PayloadVersion }, { "generation_id", generation }, { "parent_generation_id", parent }, { "expected_pointer_sha256", pointer },
                    { "run_id", session.RunIdInternal }, { "slot_id", slotId }, { "slot_kind", slotKind }, { "domain_revision", capture }, { "compatibility", session.SaveLoadService.ComponentCompatibility() },
                    { "binding", new GdDict { { "binding_version", "component-generation-binding-1" }, { "component_revision", domain.GetInt("revision").ToString(CultureInfo.InvariantCulture) },
                        { "home_ship_id", "ship_start" }, { "lifeboat_ship_id", "lifeboat" }, { "current_owner_id", owner }, { "current_location", world.CurrentLocation }, { "player_pose_owner_id", poseOwner }, { "player_local_pose", GdJson.ParseString(GdJson.Stringify(world.PlayerPositionInShip)) },
                        { "owner_revisions", revisions }, { "ship_references", references } } },
                    { "run_text", GdJson.Stringify(active.ToDict(), "  ") }, { "world_text", GdJson.Stringify(world.ToDict(), "  ") }, { "artifacts", artifacts }
                };
                return new GdDict { { "ok", true }, { "reason", "captured" }, { "payloads", payloads } };
            }
            catch (Exception e) { return new GdDict { { "ok", false }, { "reason", "capture_failed" }, { "detail", e.GetType().Name }, { "payloads", null } }; }
        }
    }
}
