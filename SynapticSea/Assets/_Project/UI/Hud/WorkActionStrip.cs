// Ported from scripts/ui/work_action_hud_panel.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// PKG-D9a work progress. Spec placement: directly above the lower-left cluster while active (not a permanent
    /// panel; Godot anchored it bottom-right). Shows verb, target, progress, interruption/blocker (caution wording +
    /// symbol) and noise. Presentation only — the session pushes WorkActionDriver state via <see cref="SetWorkState"/>.
    /// </summary>
    public sealed class WorkActionStrip : VisualElement
    {
        public const string DefaultHint = "Hold to work · release to cancel";

        bool _open;
        string _actionId = "";
        string _targetId = "";
        string _verb = "";
        double _progress;
        string _status = "idle";
        double _noise;
        string _hint = DefaultHint;

        readonly Label _title;
        readonly Meter _meter = new Meter("Progress");
        readonly StatusLine _state = new StatusLine();
        readonly Label _detail;

        public WorkActionStrip()
        {
            name = "hud-work-strip";
            AddToClassList(UiClasses.Panel);
            AddToClassList("hud-work-strip");
            pickingMode = PickingMode.Ignore;
            _title = UiFactory.Text("WORK", "hud-work-strip__title");
            _detail = UiFactory.Text("", UiClasses.LabelSecondary);
            Add(_title);
            Add(_meter);
            Add(_state);
            Add(_detail);
            Render();
        }

        public bool IsOpen() => _open;
        public double GetProgress() => _progress;
        public string GetActionId() => _actionId;
        public string GetStatus() => _status;
        public string TitleText => _title.text;
        public string StateText => _state.text;
        public Meter ProgressMeter => _meter;

        bool _compact;

        /// <summary>Disclosure at 1.5x/2x text: the hold/release hint drops (verb, target, progress, blocker, noise stay).</summary>
        public void SetCompact(bool compact)
        {
            if (_compact == compact) return;
            _compact = compact;
            EnableInClassList("hud-work-strip--compact", compact);
            Render();
        }

        public void Open()
        {
            _open = true;
            Render();
        }

        public void Close()
        {
            _open = false;
            _actionId = "";
            _targetId = "";
            _progress = 0.0;
            _status = "idle";
            Render();
        }

        /// <summary>Push live work state: action_id, target_id, verb, progress 0..1, status, noise, optional hint.</summary>
        public void SetWorkState(GdDict state)
        {
            _actionId = V.Str(state.Get("action_id", ""));
            _targetId = V.Str(state.Get("target_id", ""));
            _verb = V.Str(state.Get("verb", ""));
            _progress = GdMath.Clampf(state.GetFloat("progress", 0.0), 0.0, 1.0);
            _status = V.Str(state.Get("status", "idle"));
            _noise = System.Math.Max(0.0, state.GetFloat("noise", 0.0));
            if (state.Has("hint")) _hint = V.Str(state.Get("hint"));
            if (_status == "active" || _status == "completed")
            {
                _open = true;
            }
            else if (_status == "idle" || _status == "interrupted" || _status == "blocked")
            {
                if (_status == "idle")
                {
                    Close();
                    return;
                }
                _open = true;
            }
            Render();
        }

        /// <summary>The Godot status lines (ASCII bar kept for parity and text-only consumers).</summary>
        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (!_open && _status == "idle") return lines;
            string title = _verb.Length != 0 ? _verb.ToUpperInvariant() : "WORK";
            if (_actionId.Length != 0) title = title + " (" + _actionId + ")";
            lines.Add(title);
            if (_targetId.Length != 0) lines.Add("Target: " + _targetId);
            lines.Add("Status: " + _status);
            lines.Add("Progress: " + ProgressBar(_progress) + " " + GdString.FormatInt(GdMath.RoundI(_progress * 100.0)) + "%");
            if (_noise > 0.0) lines.Add("Noise: " + GdString.FormatFixed(_noise, 2));
            if (_hint.Length != 0 && _status == "active") lines.Add(_hint);
            return lines;
        }

        static string ProgressBar(double ratio)
        {
            int filled = (int)(GdMath.Clampf(ratio, 0.0, 1.0) * 10.0);
            var s = new System.Text.StringBuilder("[");
            for (int i = 0; i < 10; i++) s.Append(i < filled ? '#' : '-');
            return s.Append(']').ToString();
        }

        void Render()
        {
            UiFactory.SetShown(this, _open);
            _title.text = (_verb.Length == 0 ? "WORK" : _verb.ToUpperInvariant()) + (_targetId.Length != 0 ? " · " + _targetId : "")
                + (_compact ? " · " + GdString.FormatInt(GdMath.RoundI(_progress * 100.0)) + "%" : "");
            Severity sev = _status == "interrupted" || _status == "blocked" ? Severity.Caution
                : _status == "completed" ? Severity.Success : Severity.None;
            _meter.Set(_progress * 100.0, 100.0, "%", sev == Severity.Caution ? Meter.Severity.Caution : Meter.Severity.Normal);
            string stateText = _status == "active" ? "" : _status;
            _state.Set(stateText, sev);
            var detail = new List<string>();
            if (_noise > 0.0) detail.Add("Noise " + GdString.FormatFixed(_noise, 2));
            if (_hint.Length != 0 && _status == "active" && !_compact) detail.Add(_hint);
            _detail.text = string.Join(" · ", detail);
            UiFactory.SetShown(_detail, detail.Count > 0);
        }
    }
}
