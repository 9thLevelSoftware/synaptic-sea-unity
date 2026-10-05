using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    // Issued proof+wrapper admission only. SCC must still run its structural/artifact/reference validators.
    internal sealed class AdmittedContinuousSnapshots
    {
        readonly GdDict _run,_world;
        internal readonly CheckpointProofAdmission.Result PaidAdmission;
        internal ResourceAuthorityLease Lease => PaidAdmission.Lease;
        internal readonly string RunId,GenerationId,CaptureRevision;
        internal readonly long SemanticNodes,JsonUpperBytes,WireBytes,WireTokens;
        internal AdmittedContinuousSnapshots(ContinuousSnapshotAdmission.Issuer issuer,GdDict run,GdDict world,CheckpointProofAdmission.Result paid)
        {
            if(issuer==null||!issuer.Matches(run,world,paid))throw new InvalidOperationException("continuous_snapshot_issuer_mismatch");
            _run=run;_world=world;PaidAdmission=paid;SemanticNodes=issuer.SemanticNodes;JsonUpperBytes=issuer.JsonUpperBytes;WireBytes=issuer.WireBytes;WireTokens=issuer.WireTokens;RunId=(string)run.Get("run_id");GenerationId=(string)run.Get("generation_id");CaptureRevision=(string)run.Get("capture_revision");
        }
        internal GdDict CopyRun()=>_run.DeepCopy();
        internal GdDict CopyWorld()=>_world.DeepCopy();
        internal GdDict CopyOwner()=>PaidAdmission.CopyOwner();
    }
    internal static class ContinuousSnapshotAdmission
    {
        static readonly object SourceSeal=new object();
        internal static bool IsIssuerSeal(object candidate)=>ReferenceEquals(candidate,SourceSeal);
        internal sealed class Issuer
        {
            readonly GdDict _run,_world;readonly CheckpointProofAdmission.Result _paid;
            internal readonly long SemanticNodes,JsonUpperBytes,WireBytes,WireTokens;
            internal Issuer(object seal,GdDict run,GdDict world,CheckpointProofAdmission.Result paid,long semanticNodes=0,long jsonUpperBytes=0,long wireBytes=0,long wireTokens=0){if(!IsIssuerSeal(seal))throw new InvalidOperationException("foreign_continuous_snapshot_issuer");_run=run;_world=world;_paid=paid;SemanticNodes=semanticNodes;JsonUpperBytes=jsonUpperBytes;WireBytes=wireBytes;WireTokens=wireTokens;}
            internal bool Matches(GdDict run,GdDict world,CheckpointProofAdmission.Result paid)=>ReferenceEquals(_run,run)&&ReferenceEquals(_world,world)&&ReferenceEquals(_paid,paid);
        }
        static bool Text(object value)=>value is string text&&text.Length>0&&text.Length<=256&&!string.IsNullOrWhiteSpace(text);
        static bool Same(object a,object b)=>PaidSnapshotCodec.Same(a,b,PaidHashContext.BitsV2);
        internal static bool TryRead(string runText,string worldText,ProofResourceBinding currentBinding,ContinuousSaveReadPolicy policy,
            out AdmittedContinuousSnapshots admitted,out string reason)
        {
            admitted=null;reason="explicit_continuous_reader_required";
            if(policy==null||!policy.MatchesCurrentBinding(currentBinding))return false;
            if(!ContinuousSnapshotWireBudget.TryPreflight(runText,worldText,out long wireBytes,out long wireTokens,out reason))return false;
            try
            {
                // Immutable text -> privately owned parser trees; no caller mutable graph/clone race.
                if(!(GdJson.Parse(runText,true,true) is GdDict run)||!(GdJson.Parse(worldText,true,true) is GdDict world))
                {reason="continuous_snapshot_object_required";return false;}
                if(!Shapes(run,world,out reason))return false;
                var home=(GdDict)world.Get("home_ship");
                var activePaid=run.GetDictOrEmpty("crafting_summary").Get("paid_craft") as GdDict;
                var homePaid=home.GetDictOrEmpty("crafting_summary").Get("paid_craft") as GdDict;
                if(activePaid==null||homePaid==null||!Same(activePaid,homePaid)){reason="continuous_paid_mirror_mismatch";return false;}
                if(!ProofPackageCodec.TryOwnPaidEnvelope(activePaid,out var untrusted,out reason))return false;
                var package=untrusted.CopyPackage();var rawOwner=package.Get("current_owner") as GdDict;
                if(rawOwner==null||rawOwner.GetInt("schema_version")!=7){reason="continuous_owner_profile_required";return false;}
                var occurrences=new Dictionary<GdDict,GdDict>{{activePaid,SemanticPaidEnvelope(activePaid,package)},{homePaid,SemanticPaidEnvelope(homePaid,package)}};
                foreach(var snapshot in new[]{run,home,world})
                {
                    if(!(snapshot.Get("component_domain") is GdDict encoded)||encoded.GetString("schema")!=ComponentDomainCodec.BitExactSchema||
                        !ComponentDomainCodec.TryDecode(encoded,out var owner,out reason)||!Same(owner,rawOwner))
                    {reason="continuous_component_mirror_mismatch";return false;}
                    occurrences.Add(encoded,new GdDict{{"schema",encoded.Get("schema")},{"value",owner}});
                }
                var roots=new GdDict{{"run",run},{"world",world}};
                // Full decoded SOURCE occurrences; transport overhead was charged by immutable-text preflight.
                if(!ContinuousWholeSnapshotBudget.TryPreflightOwnedProjection(roots,occurrences,out long sourceNodes,out long jsonUpperBytes,out reason))return false;
                if(!policy.MatchesCurrentBinding(currentBinding)){reason="resource_epoch_changed";return false;}
                if(!PaidSnapshotCodec.TryDecodeProofEnvelope(activePaid,currentBinding,true,out var paid,out reason))return false;
                var admittedOwner=paid.CopyOwner();
                if(!Same(rawOwner,admittedOwner)||package.Get("run_id") as string!=run.Get("run_id") as string||
                    package.Get("actor_id") as string!="player_local")
                {reason="continuous_owner_identity_mismatch";return false;}
                if(!policy.MatchesCurrentBinding(currentBinding)){reason="resource_epoch_changed";return false;}
                admitted=new AdmittedContinuousSnapshots(new Issuer(SourceSeal,run,world,paid,sourceNodes,jsonUpperBytes,wireBytes,wireTokens),run,world,paid);reason="continuous_proof_and_wrapper_admitted";return true;
            }
            catch(GdJson.ParseError){reason="continuous_snapshot_json_invalid";return false;}
            catch(ArgumentException){reason="continuous_snapshot_invalid";return false;}
            catch(OverflowException){reason="continuous_snapshot_counter_overflow";return false;}
        }
        internal static GdDict SemanticPaidEnvelope(GdDict paid,GdDict decodedPackage)
            =>new GdDict{{"schema_version",paid.Get("schema_version")},{"save_mode",paid.Get("save_mode")},
                {"domain",new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",new GdDict{{"schema",ComponentDomainCodec.BitExactSchema},{"value",decodedPackage}}}}}};
        static bool Shapes(GdDict run,GdDict world,out string reason)
        {
            reason="continuous_snapshot_shape";
            if(!ContinuousSnapshotFieldPlan.InspectBootstrapLayout(run,false,out _,out reason)||
                !ContinuousSnapshotFieldPlan.InspectBootstrapLayout(world,true,out _,out reason)||
                !(world.Get("home_ship") is GdDict home)||!ContinuousSnapshotFieldPlan.InspectBootstrapLayout(home,false,out _,out reason))return false;
            reason="continuous_snapshot_shape";
            foreach(var key in RequiredRun)if(!run.Has(key)||!home.Has(key))return false;
            foreach(var key in RequiredWorld)if(!world.Has(key))return false;
            if(run.Get("slice_version") as string!=RunSnapshot.ComponentIntegrationVersion||home.Get("slice_version") as string!=RunSnapshot.ComponentIntegrationVersion||
                world.Get("slice_version") as string!=WorldSnapshot.ComponentIntegrationVersion)return false;
            foreach(var key in new[]{"run_id","generation_id","capture_revision","godot_version"})
                if(!Text(run.Get(key))||!Same(run.Get(key),home.Get(key))||!Same(run.Get(key),world.Get(key)))return false;
            if(!long.TryParse((string)run.Get("capture_revision"),NumberStyles.None,CultureInfo.InvariantCulture,out long revision)||revision<1||
                revision.ToString(CultureInfo.InvariantCulture)!=(string)run.Get("capture_revision"))return false;
            if(!Same(run.Get("current_location"),world.Get("current_location"))||home.Get("current_location") as string!=""||
                !Same(run.Get("player_position"),world.Get("player_position_in_ship")))return false;
            reason="";return true;
        }
        static readonly string[] RequiredRun={"layout_path","kit_path","gameplay_slice_path","player_position","current_objective_sequence","ship_systems_summary","route_control_summary","oxygen_summary","inventory_summary","fire_summary","electrical_arc_summary","objective_progress_summary","player_progression_summary","skill_tree_summary","settings_summary","audio_summary","spoilage_summary","hydroponics_summary","water_recycler_summary","crafting_summary","material_summary","consumable_summary","medicine_summary","stimulant_summary","addiction_summary","ammo_summary","utility_summary","vitals_summary","sanity_summary","radiation_summary","temperature_summary","status_effects_summary","hallucination_summary","module_integrity_summary","component_placement_summary","work_action_summary","ship_modification_summary","wound_summary","web_chart_summary","tutorial_summary","equipment_summary","home_looted_containers","home_ship_inventory","run_context","home_ship_carts","home_breach_environment","meta_progression_summary","unique_item_summary","visited_ships","play_time_seconds","current_location","world_seed","slot_id","slot_kind","is_autosave","is_quicksave","parent_world_slot","run_id","slice_version","godot_version","saved_at","saved_at_epoch","component_domain","generation_id","capture_revision"};
        static readonly string[] RequiredWorld={"world_summary","home_ship","meta_progression_summary","unique_item_summary","home_looted_containers","home_ship_inventory","home_ship_carts","home_breach_environment","player_equipment","visited_ships","current_location","world_time","player_position_in_ship","dock_edges","piloted_ship_id","aboard_ship_id","opened_ports","run_id","slice_version","godot_version","saved_at","component_domain","generation_id","capture_revision"};
    }
}
