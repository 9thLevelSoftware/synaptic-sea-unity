// Ported from scripts/interaction/interactable.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// The objective / objective-step interaction node (Godot <c>Interactable</c>). Completes once when the player
    /// interacts in range (Area3D overlap or the lenient direct-distance gate) while active.
    /// </summary>
    public sealed class ObjectiveInteractable : SessionInteractable
    {
        public override string Kind => "objective";

        /// <summary>signal interaction_completed(interaction_id, objective_id, sequence, objective_type, room_id, step_id)</summary>
        public event Action<string, string, long, string, string, string> InteractionCompleted;

        public string InteractionId = "";
        public string ObjectiveId = "";
        public long Sequence = 0;
        public string ObjectiveType = "";
        public string RoomId = "";
        public string PromptText = "Interact";
        public string TooltipSubjectId = "";
        public bool Completed = false;
        public bool Active = true;
        public bool MarkerVisible = false;
        public string StepId = "";
        public bool IsStep = false;

        /// <summary>set_meta("placement_id", ...) — the objective's placement id (meta only in Godot).</summary>
        public string PlacementId = "";

        /// <summary>RUNTIME: <c>marker.visible</c>.</summary>
        public bool MarkerShown => MarkerVisible;

        /// <summary>
        /// RUNTIME: which marker material <c>_refresh_marker_material</c> picks: "completed" (grey 0.4,0.4,0.4,0.16),
        /// "active" (green 0.25,0.95,0.45,0.32) or "inactive" (blue 0.25,0.55,0.95,0.12).
        /// </summary>
        public string MarkerMaterialState => Completed ? "completed" : (Active ? "active" : "inactive");

        public void ConfigureFromObjective(GdDict objective, Vec3 worldPosition, double radius = 1.8)
        {
            objective = objective ?? new GdDict();
            ObjectiveId = V.Str(objective.Get("id", ""));
            Sequence = V.I64(objective.Get("sequence", 0L));
            ObjectiveType = V.Str(objective.Get("type", "objective"));
            RoomId = V.Str(objective.Get("room_id", ""));
            InteractionId = "objective:" + GdString.FormatIntPadded(Sequence, 2) + ":" + ObjectiveId;
            PromptText = "Interact: " + ObjectiveType;
            TooltipSubjectId = ObjectiveType;
            Active = true;
            InteractionRadius = radius;
            Completed = false;
            CandidatePlayerInRange = false;
            NodeName = "Interactable_seq" + GdString.FormatInt(Sequence) + "_" + ObjectiveType;
            LocalPosition = worldPosition;
            PlacementId = V.Str(objective.Get("placement_id", ""));
            // RUNTIME: set_meta(interaction_id/objective_id/objective_sequence/objective_type/placement_id/room_id);
            // sphere collision (radius) + debug sphere marker (radius, visible = marker_visible, material per MarkerMaterialState).
        }

        public void ConfigureFromStep(GdDict objective, GdDict step, Vec3 worldPosition, double radius = 1.8)
        {
            ConfigureFromObjective(objective, worldPosition, radius);
            step = step ?? new GdDict();
            IsStep = true;
            StepId = V.Str(step.Get("step_id", ""));
            if (StepId.Length == 0)
                StepId = "step_" + InteractionId;
            InteractionId = InteractionId + ":" + StepId;
            PromptText = "Repair: " + StepId;
            TooltipSubjectId = "junction_step";
            NodeName = "Interactable_seq" + GdString.FormatInt(Sequence) + "_step_" + StepId;
            PlacementId = V.Str((objective ?? new GdDict()).Get("placement_id", ""));
            // RUNTIME: set_meta("step_id"), set_meta("placement_id"), set_meta("is_step", true).
        }

        public void SetActive(bool isActive)
        {
            Active = isActive;
            // RUNTIME: set_meta("active", active); _refresh_marker_material() (see MarkerMaterialState).
            NotifyChanged();
        }

        public void SetMarkerVisible(bool isVisible)
        {
            MarkerVisible = isVisible;
            // RUNTIME: marker.visible = marker_visible.
            NotifyChanged();
        }

        public bool TryInteract(Vec3 playerPosition)
        {
            if (Completed || !Active)
                return false;
            if (!CandidatePlayerInRange && !IsPlayerInDirectRangeLenient(playerPosition))
                return false;
            Completed = true;
            SetActive(false);
            InteractionCompleted?.Invoke(InteractionId, ObjectiveId, Sequence, ObjectiveType, RoomId, StepId);
            return true;
        }
    }
}
