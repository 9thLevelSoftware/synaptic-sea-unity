using System;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Closed actual serializer layout only. This plan grants NO capture/admission authority.
    internal enum ContinuousSnapshotWriter { OutputMetadata, PoseAndTopology, ResourceAndRunContext, ProtectedParticipants, Survival, RetainedWorld, SimulationClock, OtherModelWriter }
    internal sealed class ContinuousSnapshotFieldPlan
    {
        readonly string[] _names;
        readonly ContinuousSnapshotWriter[] _writers;
        internal int Count=>_names.Length;
        internal string Name(int index)=>_names[index];
        internal ContinuousSnapshotWriter Writer(int index)=>_writers[index];
        ContinuousSnapshotFieldPlan(string[] names,ContinuousSnapshotWriter[] writers){_names=names;_writers=writers;}
        static bool Classify(bool world,string field,out ContinuousSnapshotWriter writer)
        {
            writer=default;
            switch((world?"world:":"run:")+field)
            {
                case "run:layout_path":writer=ContinuousSnapshotWriter.ResourceAndRunContext;return true;
                case "run:kit_path":writer=ContinuousSnapshotWriter.ResourceAndRunContext;return true;
                case "run:gameplay_slice_path":writer=ContinuousSnapshotWriter.ResourceAndRunContext;return true;
                case "run:player_position":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "run:current_objective_sequence":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:ship_systems_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:route_control_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:oxygen_summary":writer=ContinuousSnapshotWriter.Survival;return true;
                case "run:inventory_summary":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:fire_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:electrical_arc_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:objective_progress_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:player_progression_summary":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:skill_tree_summary":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:settings_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:audio_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:spoilage_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:hydroponics_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:water_recycler_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:crafting_summary":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:material_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:consumable_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:medicine_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:stimulant_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:addiction_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:ammo_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:utility_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:vitals_summary":writer=ContinuousSnapshotWriter.Survival;return true;
                case "run:sanity_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:radiation_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:temperature_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:status_effects_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:hallucination_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:module_integrity_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:component_placement_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:work_action_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:ship_modification_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:wound_summary":writer=ContinuousSnapshotWriter.Survival;return true;
                case "run:web_chart_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:tutorial_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:equipment_summary":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:home_looted_containers":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "run:home_ship_inventory":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "run:run_context":writer=ContinuousSnapshotWriter.ResourceAndRunContext;return true;
                case "run:home_ship_carts":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "run:home_breach_environment":writer=ContinuousSnapshotWriter.Survival;return true;
                case "run:meta_progression_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:unique_item_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "run:visited_ships":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "run:play_time_seconds":writer=ContinuousSnapshotWriter.SimulationClock;return true;
                case "run:current_location":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "run:world_seed":writer=ContinuousSnapshotWriter.ResourceAndRunContext;return true;
                case "run:slot_id":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:slot_kind":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:is_autosave":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:is_quicksave":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:parent_world_slot":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:run_id":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:slice_version":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:godot_version":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:saved_at":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:saved_at_epoch":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:home_finite_loot":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "run:home_portal_state":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "run:component_domain":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "run:generation_id":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "run:capture_revision":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:world_summary":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:home_ship":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:meta_progression_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "world:unique_item_summary":writer=ContinuousSnapshotWriter.OtherModelWriter;return true;
                case "world:home_looted_containers":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:home_ship_inventory":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:home_ship_carts":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:home_breach_environment":writer=ContinuousSnapshotWriter.Survival;return true;
                case "world:player_equipment":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "world:visited_ships":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:current_location":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:world_time":writer=ContinuousSnapshotWriter.SimulationClock;return true;
                case "world:player_position_in_ship":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:dock_edges":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:piloted_ship_id":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:aboard_ship_id":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:opened_ports":writer=ContinuousSnapshotWriter.PoseAndTopology;return true;
                case "world:run_id":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:slice_version":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:godot_version":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:saved_at":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:mobile_home_state":writer=ContinuousSnapshotWriter.RetainedWorld;return true;
                case "world:component_domain":writer=ContinuousSnapshotWriter.ProtectedParticipants;return true;
                case "world:generation_id":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                case "world:capture_revision":writer=ContinuousSnapshotWriter.OutputMetadata;return true;
                default:return false;
            }
        }
        internal static bool InspectBootstrapLayout(GdDict actualBootstrapSnapshot,bool world,out ContinuousSnapshotFieldPlan plan,out string reason)
        {
            plan=null;reason="invalid_capture_layout";
            if(actualBootstrapSnapshot==null||actualBootstrapSnapshot.Count<1||actualBootstrapSnapshot.Count>128)return false;
            var names=new string[actualBootstrapSnapshot.Count];var writers=new ContinuousSnapshotWriter[names.Length];int i=0;
            foreach(var row in actualBootstrapSnapshot)
            {
                if(!(row.Key is string field)||!Classify(world,field,out var writer)){reason="unenrolled_snapshot_field";return false;}
                names[i]=field;writers[i++]=writer;
            }
            plan=new ContinuousSnapshotFieldPlan(names,writers);reason="";return true;
        }
        // Until concrete actual producers for EVERY member are enrolled, whole-world is refused.
        internal bool TryBuildWholeWorldSave(out string reason){reason="continuous_world_producers_not_enrolled";return false;}
    }
}
