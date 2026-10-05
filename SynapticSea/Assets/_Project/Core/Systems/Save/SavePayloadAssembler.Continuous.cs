using System;
using System.Globalization;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public static partial class SavePayloadAssembler
    {
        // Known ordinary saved DTO fields only. Native model/cache objects are never changed.
        internal static void NormalizeContinuousPlacementCut(GdDict run,GdDict world,ref GdDict homePlacement)
        {
            run["component_placement_summary"]=PaidPlacementSummary(run.GetDictOrEmpty("component_placement_summary"));
            run["visited_ships"]=PaidRetainedPlacements(run.GetDictOrEmpty("visited_ships"));
            var home=world.GetDictOrEmpty("home_ship");
            home["component_placement_summary"]=PaidPlacementSummary(home.GetDictOrEmpty("component_placement_summary"));
            home["visited_ships"]=PaidRetainedPlacements(home.GetDictOrEmpty("visited_ships"));
            world["visited_ships"]=PaidRetainedPlacements(world.GetDictOrEmpty("visited_ships"));
            var boat=world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat");
            if(boat.Get("component_placement") is GdDict placement)boat["component_placement"]=PaidPlacementSummary(placement);
            homePlacement=PaidPlacementSummary(homePlacement);
        }
        // Owned worker data only. No RunSession, Unity/scene getters, live summaries or storage reads.
        internal static GdDict BuildContinuous(OwnedContinuousCapture source,CheckpointProofAdmission.Result proof)
        {
            if(source==null||proof==null||!source.IsCurrent||!proof.Lease.IsCurrent)throw new InvalidOperationException("continuous_source_retired");
            GdDict domain=proof.CopyOwner();
            if(!PaidSnapshotCodec.TryCreateProofEnvelope(proof,true,out GdDict envelope,out string reason))throw new InvalidOperationException(reason);
            GdDict encoded=ComponentDomainCodec.Encode(domain,ComponentDomainCodec.BitExactSchema);
            GdDict active=source.CopyRun(),world=source.CopyWorld(),references=source.CopyReferences();
            GdDict home=world.GetDictOrEmpty("home_ship").DeepCopy(),homeRef=references.GetDictOrEmpty("ship_start"),activeRef=references.GetDictOrEmpty(source.CurrentOwner);
            GdDict participants=domain.GetDictOrEmpty("participating_state");
            var pose=world.GetArrayOrEmpty("player_position_in_ship");
            active["objective_progress_summary"]=PaidObjectiveSummary(active.GetDictOrEmpty("objective_progress_summary"));
            home["objective_progress_summary"]=PaidObjectiveSummary(home.GetDictOrEmpty("objective_progress_summary"));
            active["layout_path"]=activeRef.Get("layout_path");active["gameplay_slice_path"]=activeRef.Get("gameplay_slice_path");active["kit_path"]=activeRef.Get("kit_path");
            active["current_location"]=world.Get("current_location");active["visited_ships"]=world.GetDictOrEmpty("visited_ships").DeepCopy();active["player_position"]=pose.DeepCopy();
            active["run_id"]=source.RunId;active["slot_id"]=source.SlotId;active["slot_kind"]=source.SlotKind;active["is_autosave"]=source.SlotKind=="auto";active["is_quicksave"]=source.SlotKind=="quick";
            active["slice_version"]=RunSnapshot.ComponentIntegrationVersion;active["generation_id"]=source.Generation;active["capture_revision"]=source.CaptureText;active["component_domain"]=encoded.DeepCopy();
            GdDict crafting=PaidCraftingSummary(active.GetDictOrEmpty("crafting_summary"),participants);crafting["paid_craft"]=envelope.DeepCopy();active["crafting_summary"]=crafting;
            active["saved_at_epoch"]=source.SavedAtEpoch;
            GdDict awaySystems=source.CopyAwaySystems();if(!awaySystems.IsEmpty)active["ship_systems_summary"]=awaySystems;
            home["layout_path"]=homeRef.Get("layout_path");home["gameplay_slice_path"]=homeRef.Get("gameplay_slice_path");home["kit_path"]=homeRef.Get("kit_path");
            home["current_location"]="";home["run_id"]=source.RunId;if(world.GetString("current_location").Length==0)home["player_position"]=pose.DeepCopy();
            home["slice_version"]=RunSnapshot.ComponentIntegrationVersion;home["generation_id"]=source.Generation;home["capture_revision"]=source.CaptureText;home["component_domain"]=encoded.DeepCopy();
            crafting=PaidCraftingSummary(home.GetDictOrEmpty("crafting_summary"),participants);crafting["paid_craft"]=envelope.DeepCopy();home["crafting_summary"]=crafting;
            GdDict systems=home.GetDictOrEmpty("ship_systems_summary");foreach(var item in source.CopyHomeSystems())systems[item.Key]=item.Value;home["ship_systems_summary"]=systems;
            GdDict placement=source.CopyHomePlacement();if(!placement.IsEmpty)home["component_placement_summary"]=placement;
            active["component_placement_summary"]=PaidPlacementSummary(active.GetDictOrEmpty("component_placement_summary"));home["component_placement_summary"]=PaidPlacementSummary(home.GetDictOrEmpty("component_placement_summary"));
            home["visited_ships"]=PaidRetainedPlacements(home.GetDictOrEmpty("visited_ships"));world["visited_ships"]=PaidRetainedPlacements(world.GetDictOrEmpty("visited_ships"));active["visited_ships"]=world.GetDictOrEmpty("visited_ships").DeepCopy();
            GdDict boat=world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat");if(boat.Get("component_placement") is GdDict boatPlacement)boat["component_placement"]=PaidPlacementSummary(boatPlacement);
            world["home_ship"]=home;world["run_id"]=source.RunId;world["slice_version"]=WorldSnapshot.ComponentIntegrationVersion;world["generation_id"]=source.Generation;world["capture_revision"]=source.CaptureText;world["component_domain"]=encoded.DeepCopy();
            var revisions=new GdDict();foreach(object owner in references.Keys)revisions[owner]=source.CaptureRevision;
            var binding=new GdDict{{"binding_version","component-generation-binding-1"},{"component_revision",domain.GetInt("revision").ToString(CultureInfo.InvariantCulture)},
                {"home_ship_id","ship_start"},{"lifeboat_ship_id","lifeboat"},{"current_owner_id",source.CurrentOwner},{"current_location",world.Get("current_location")},{"player_pose_owner_id",source.PoseOwner},{"player_local_pose",pose.DeepCopy()},{"owner_revisions",revisions},{"ship_references",references},
                {"save_mode",PaidSnapshotCodec.DiagnosticMode},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm}};
            return new GdDict{{"schema_version",SaveCommitCoordinator.PayloadVersion},{"generation_id",source.Generation},{"parent_generation_id",source.Parent.ParentGeneration},{"expected_pointer_sha256",source.Parent.ExpectedPointer},
                {"run_id",source.RunId},{"slot_id",source.SlotId},{"slot_kind",source.SlotKind},{"domain_revision",source.CaptureRevision},{"compatibility",source.CopyCompatibility()},{"binding",binding},
                {"run_text",PaidSnapshotCodec.Stringify(active,PaidSnapshotCodec.Policy.Raw,PaidHashContext.BitsV2)},{"world_text",PaidSnapshotCodec.Stringify(world,PaidSnapshotCodec.Policy.Raw,PaidHashContext.BitsV2)},{"artifacts",source.CopyArtifacts()}};
        }
    }
}
