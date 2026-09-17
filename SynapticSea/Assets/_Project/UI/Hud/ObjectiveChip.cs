// Ported from scripts/ui/objective_tracker.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Upper-left objective chip: ONE actionable line (the current step) plus a compact progress count.
    ///
    /// Spec departure from Godot (the proposed layout ADR supersedes ADR-0027's tracker contents): the Godot tracker
    /// was a 520×250 panel carrying a title, a controls legend, progress, system-status lines, the current objective and
    /// the interaction prompt. Here the chip shows only the current objective; the interaction prompt is raised through
    /// <see cref="PromptChanged"/> for the transient context-prompt slot above the HUD cluster; the full composed text
    /// (controls, systems, history) stays available through <see cref="GetHudText"/> / <see cref="GetDetailLines"/> for
    /// inspection surfaces. Same model API as the Godot tracker.
    /// </summary>
    public sealed class ObjectiveChip : VisualElement
    {
        public const string DefaultPrompt = "Approach the highlighted objective and press E.";
        public const string CompletePrompt = "Slice complete. Extraction route found.";
        public const string ControlsLine = "Controls: WASD or Arrows move / E or Enter or Space interact / F5 save / F9 load";

        GdArray _objectives = new GdArray();
        readonly HashSet<long> _completed = new HashSet<long>();
        bool _runComplete;
        long _currentSequence = 1;
        string _interactionPrompt = DefaultPrompt;
        List<string> _systemStatusLines = new List<string>();
        GdDict _stepProgress = new GdDict();

        readonly Label _marker;
        readonly Label _line;
        readonly Label _progress;

        /// <summary>The interaction prompt changed (the HUD shows it as a transient context prompt).</summary>
        public event Action<string> PromptChanged;

        public ObjectiveChip()
        {
            name = "hud-objective-chip";
            AddToClassList(UiClasses.Panel);
            AddToClassList("hud-objective-chip");
            pickingMode = PickingMode.Ignore;
            _marker = UiFactory.Text("▶", "hud-objective-chip__marker", UiClasses.LabelMono);
            _line = UiFactory.Text("", "hud-objective-chip__text");
            _progress = UiFactory.Text("", "hud-objective-chip__progress", UiClasses.LabelSecondary, UiClasses.LabelMono);
            Add(_marker);
            Add(_line);
            Add(_progress);
            Refresh();
        }

        public string ChipText => _line.text;
        public string ProgressText => _progress.text;
        public string InteractionPrompt => _interactionPrompt;
        public bool RunComplete => _runComplete;
        public long CurrentSequence => _currentSequence;

        public void SetObjectives(GdArray objectiveList)
        {
            _objectives = objectiveList?.DeepCopy() ?? new GdArray();
            _completed.Clear();
            _runComplete = false;
            _currentSequence = 1;
            _systemStatusLines = new List<string>();
            SetPromptInternal(DefaultPrompt);
            Refresh();
        }

        public void SetSystemStatusLines(IEnumerable<string> lines)
        {
            _systemStatusLines = new List<string>(lines ?? Array.Empty<string>());
            Refresh();
        }

        public void SetCurrentSequence(long sequence)
        {
            _currentSequence = Math.Max(sequence, 1);
            Refresh();
        }

        public void SetStepProgress(long sequence, GdDict progress)
        {
            _stepProgress = progress?.DeepCopy() ?? new GdDict();
            Refresh();
        }

        public void SetInteractionPrompt(string text)
        {
            SetPromptInternal(text ?? "");
            Refresh();
        }

        public void MarkCompleted(long sequence)
        {
            _completed.Add(sequence);
            Refresh();
        }

        public void MarkRunComplete()
        {
            _runComplete = true;
            SetPromptInternal(CompletePrompt);
            Refresh();
        }

        public int GetCompletedCount() => _completed.Count;

        public bool IsSequenceCompleted(long sequence) => _completed.Contains(sequence);

        /// <summary>The Godot tracker's full composed text (kept for inspection/detail and parity checks).</summary>
        public string GetHudText() => string.Join("\n", GetDetailLines());

        public List<string> GetDetailLines()
        {
            var lines = new List<string>
            {
                "Synaptic Sea First Playable",
                ControlsLine,
                "Progress: " + GdString.FormatInt(_completed.Count) + "/" + GdString.FormatInt(_objectives.Count),
            };
            if (_systemStatusLines.Count > 0)
            {
                lines.Add("Systems:");
                foreach (string line in _systemStatusLines) lines.Add("  " + line);
            }
            lines.Add(_runComplete ? "Current: COMPLETE - Extraction route found" : "Current: " + CurrentObjectiveDisplay());
            lines.Add("Prompt: " + _interactionPrompt);
            return lines;
        }

        void SetPromptInternal(string text)
        {
            if (_interactionPrompt == text) return;
            _interactionPrompt = text;
            PromptChanged?.Invoke(text);
        }

        void Refresh()
        {
            _line.text = _runComplete ? "COMPLETE — Extraction route found" : CurrentObjectiveDisplay();
            _marker.text = _runComplete ? "✓" : "▶";
            _progress.text = GdString.FormatInt(_completed.Count) + "/" + GdString.FormatInt(_objectives.Count);
            UiFactory.SetShown(_progress, _objectives.Count > 0);
            SeverityText.Apply(this, _runComplete ? Severity.Success : Severity.None);
        }

        public string CurrentObjectiveDisplay()
        {
            foreach (object objectiveVariant in _objectives)
            {
                if (!(objectiveVariant is GdDict objective)) continue;
                long sequence = objective.GetInt("sequence", 0);
                if (sequence != _currentSequence) continue;
                long requiredSteps = _stepProgress.GetInt("required_steps", 1);
                long completedSteps = _stepProgress.GetInt("completed_steps", 0);
                string label = ObjectiveLabel(objective);
                if (requiredSteps > 1)
                    label = label + " (" + GdString.FormatInt(completedSteps) + "/" + GdString.FormatInt(requiredSteps) + ")";
                return GdString.FormatIntPadded(sequence, 2) + " " + label + " @ " + RoomDisplay(V.Str(objective.Get("room_id", "room")));
            }
            return GdString.FormatIntPadded(_currentSequence, 2) + " Objective";
        }

        /// <summary>REQ-011: kind "repair_junction" reads "Repair junction" although the type stays restore_systems.</summary>
        static string ObjectiveLabel(GdDict objective)
        {
            if (V.Str(objective.Get("kind", "")) == "repair_junction") return "Repair junction";
            return TypeDisplay(V.Str(objective.Get("type", "objective")));
        }

        static string TypeDisplay(string rawType)
        {
            var words = new List<string>();
            foreach (string part in GdString.Split(rawType, "_", false)) words.Add(GdString.Capitalize(part));
            return string.Join(" ", words);
        }

        static string RoomDisplay(string roomId) => GdString.Capitalize(roomId.Replace("_", " "));
    }
}
