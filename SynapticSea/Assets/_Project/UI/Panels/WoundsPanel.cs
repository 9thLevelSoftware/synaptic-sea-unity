// Ported from scripts/ui/wounds_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// The session's wound treatment surface (<c>RunSession.GetTreatableWounds / BandageWound / TreatWound</c>): the
    /// session picks and consumes the item, plays the SFX, emits the training event and explains refusals.
    /// </summary>
    public interface IWoundTreatmentHost
    {
        WoundState WoundState { get; }

        /// <summary>Open wounds with <c>can_bandage / bandage_item_id / bandage_reason / can_treat / treat_item_id / treat_reason</c>.</summary>
        GdArray GetTreatableWounds();

        /// <summary><c>{ok, action, wound_id, item_id, reason}</c>.</summary>
        GdDict BandageWound(string woundId);

        /// <summary><c>{ok, action, wound_id, item_id, reason}</c>.</summary>
        GdDict TreatWound(string woundId);
    }

    /// <summary>
    /// PKG-D9d survivor wounds (LIVE inspection). Wound list + Bandage / Treat through the pure <see cref="WoundState"/>
    /// calls, with the Godot deny paths ("no wound", "bandage failed", "treat failed"). Spec: critical condition first
    /// (rows are ranked by severity, stable), body part shown only because the model supplies it, severity carries
    /// wording + symbol, and Bandage/Treat are focusable buttons (plus Submit = Treat) for keyboard and gamepad.
    /// In play the panel is bound to an <see cref="IWoundTreatmentHost"/> (the session): each row names the item a
    /// treatment will use or why it is refused, and the buttons go through the host so items are consumed. Binding a bare
    /// <see cref="WoundState"/> keeps the Godot model-only path (tests, tools).
    /// </summary>
    public sealed class WoundsPanel : SurfacePanel
    {
        public event Action PanelClosed;
        public event Action<string, string> TreatmentApplied;

        WoundState _woundState;
        IWoundTreatmentHost _host;
        bool _statusDeny;
        bool _open;
        int _selected;
        string _status = "";
        IUiAudio _audio;

        readonly Label _summary;
        readonly SelectableList _list;
        readonly Button _bandage;
        readonly Button _treat;

        public WoundsPanel() : base("WOUNDS", SurfaceTime.Live)
        {
            AddToClassList("ss-wounds");
            _summary = UiFactory.Text("", UiClasses.LabelSecondary, UiClasses.LabelMono);
            Body.Add(_summary);
            _list = new SelectableList("wound", "No active wounds.");
            Body.Add(_list);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            _bandage = UiFactory.Button("Bandage", () => BandageSelected(), "act:bandage");
            _treat = UiFactory.Button("Treat", () => TreatSelected(), "act:treat");
            tools.Add(_bandage);
            tools.Add(_treat);
            Body.Add(tools);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
            _list.ActivateRequested += i =>
            {
                _selected = i;
                TreatSelected();
            };
            Render();
        }

        public override string SurfaceId => "wounds";

        public void Bind(WoundState woundState)
        {
            _host = null;
            _woundState = woundState;
            Render();
        }

        /// <summary>Binds the session treatment surface (the panel reads its wound model through it).</summary>
        public void Bind(IWoundTreatmentHost host)
        {
            _host = host;
            _woundState = host?.WoundState;
            Render();
        }

        public IWoundTreatmentHost Host => _host;

        /// <summary>The last treatment result from the host (empty before one).</summary>
        public GdDict LastResult { get; private set; } = new GdDict();

        public void SetAudioManager(IUiAudio audio) => _audio = audio;

        public bool IsOpen() => _open;

        public void Open()
        {
            _open = true;
            SetViewVisible(true);
            _selected = 0;
            _status = "";
            _statusDeny = false;
            Render();
        }

        public void Close()
        {
            _open = false;
            SetViewVisible(false);
            PanelClosed?.Invoke();
        }

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        protected override void RequestClose() => Close();

        public void Refresh() => Render();

        public int GetSelectedIndex() => _selected;

        public string GetSelectedWoundId()
        {
            List<GdDict> rows = WoundRows();
            if (_selected < 0 || _selected >= rows.Count) return "";
            return V.Str(rows[_selected].Get("wound_id", ""));
        }

        public void MoveSelection(int delta)
        {
            int n = WoundRows().Count;
            if (n <= 0)
            {
                _selected = 0;
                Render();
                return;
            }
            _selected = (int)GdMath.Clampi(_selected + delta, 0, n - 1);
            Render();
        }

        public bool BandageSelected()
        {
            string wid = GetSelectedWoundId();
            if (wid.Length == 0 || _woundState == null)
            {
                SetStatus("no wound", true);
                Render();
                PlayDeny();
                return false;
            }
            if (_host != null) return ApplyThroughHost("bandage", wid);
            if (!_woundState.Bandage(wid))
            {
                SetStatus("bandage failed", true);
                Render();
                PlayDeny();
                return false;
            }
            SetStatus("bandaged " + wid, false);
            Reanchor(wid);
            TreatmentApplied?.Invoke(wid, "bandage");
            Render();
            return true;
        }

        public bool TreatSelected(double severityReduce = 0.35)
        {
            string wid = GetSelectedWoundId();
            if (wid.Length == 0 || _woundState == null)
            {
                SetStatus("no wound", true);
                Render();
                PlayDeny();
                return false;
            }
            if (_host != null) return ApplyThroughHost("treat", wid);
            if (!_woundState.Treat(wid, severityReduce))
            {
                SetStatus("treat failed", true);
                Render();
                PlayDeny();
                return false;
            }
            SetStatus("treated " + wid, false);
            Reanchor(wid);
            TreatmentApplied?.Invoke(wid, "treat");
            Render();
            return true;
        }

        /// <summary>
        /// The session path: the host consumes the item and plays the cue (including the refusal cue), so the panel only
        /// reports. Status: "bandaged w1 with bandage_kit" or "cannot bandage: no bandage item".
        /// </summary>
        bool ApplyThroughHost(string action, string woundId)
        {
            GdDict result = action == "bandage" ? _host.BandageWound(woundId) : _host.TreatWound(woundId);
            LastResult = result ?? new GdDict();
            bool ok = LastResult.GetBool("ok");
            string item = LastResult.GetString("item_id");
            if (ok)
            {
                SetStatus((action == "bandage" ? "bandaged " : "treated ") + woundId + (item.Length != 0 ? " with " + item : ""), false);
                Reanchor(woundId);
                TreatmentApplied?.Invoke(woundId, action);
            }
            else
            {
                SetStatus("cannot " + action + ": " + ReasonText(LastResult.GetString("reason")), true);
            }
            Render();
            return ok;
        }

        void SetStatus(string text, bool deny)
        {
            _status = text ?? "";
            _statusDeny = deny;
        }

        /// <summary>Player wording for a session refusal reason (<c>no_bandage_item</c> → "no bandage item").</summary>
        public static string ReasonText(string reason)
        {
            switch (reason ?? "")
            {
                case "": return "unavailable";
                case "no_bandage_item": return "no bandage item";
                case "no_treatment_item": return "no medical item";
                case "already_bandaged": return "already bandaged";
                case "already_treated": return "already treated";
                case "wound_healed": return "wound healed";
                case "unknown_wound": return "wound not found";
                case "wounds_unavailable": return "wounds unavailable";
                default: return reason.Replace('_', ' ');
            }
        }

        /// <summary>Keeps the cursor on the same wound (stable identity) after a treatment re-ranks the list; when the
        /// wound healed out of the list, the cursor stays on the deterministic adjacent row.</summary>
        void Reanchor(string woundId)
        {
            List<GdDict> rows = WoundRows();
            for (int i = 0; i < rows.Count; i++)
            {
                if (V.Str(rows[i].Get("wound_id", "")) == woundId)
                {
                    _selected = i;
                    return;
                }
            }
            _selected = (int)GdMath.Clampi(_selected, 0, Math.Max(0, rows.Count - 1));
        }

        void PlayDeny() => _audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        public string GetStatus() => _status;

        public List<string> GetStatusLines()
        {
            var lines = new List<string> { "WOUNDS" };
            if (_woundState == null)
            {
                lines.Add("(no wound model)");
                return lines;
            }
            lines.AddRange(_woundState.GetStatusLines());
            List<GdDict> rows = WoundRows();
            if (rows.Count == 0)
            {
                lines.Add("(no active wounds)");
            }
            else
            {
                lines.Add("Select: " + GetSelectedWoundId());
                lines.Add("[B] bandage  [T] treat");
            }
            if (_status.Length != 0) lines.Add(_status);
            lines.Add("Work speed ×" + GdString.FormatFixed(_woundState.WorkSpeedMultiplier(), 2));
            lines.Add("Bleed " + GdString.FormatFixed(_woundState.TotalBleedRate(), 2));
            return lines;
        }

        /// <summary>Active wounds (severity &gt; 0.001), ranked most severe first (stable for ties).</summary>
        List<GdDict> WoundRows()
        {
            var output = new List<GdDict>();
            if (_woundState == null) return output;
            IEnumerable<object> source = _host != null ? (IEnumerable<object>)_host.GetTreatableWounds() : _woundState.Wounds;
            foreach (object w in source)
            {
                if (!(w is GdDict e)) continue;
                if (e.GetFloat("severity", 0.0) <= 0.001) continue;
                output.Add(e);
            }
            var ranked = new List<GdDict>(output);
            // Stable insertion sort by severity desc (critical condition first).
            for (int i = 1; i < ranked.Count; i++)
            {
                GdDict cur = ranked[i];
                int j = i - 1;
                while (j >= 0 && ranked[j].GetFloat("severity", 0.0) < cur.GetFloat("severity", 0.0))
                {
                    ranked[j + 1] = ranked[j];
                    j--;
                }
                ranked[j + 1] = cur;
            }
            return ranked;
        }

        public static Severity SeverityFor(double severity) =>
            severity >= 0.6 ? Severity.Danger : severity >= 0.3 ? Severity.Caution : Severity.Info;

        /// <summary>The Godot row text (kept for parity checks): "wid kind@part sev=0.50 [TB]".</summary>
        public List<string> GetRowTexts()
        {
            var texts = new List<string>();
            foreach (GdDict e in WoundRows())
            {
                string flags = "";
                if (e.GetBool("treated", false)) flags += "T";
                if (e.GetBool("bandaged", false)) flags += "B";
                if (flags.Length == 0) flags = "-";
                texts.Add(V.Str(e.Get("wound_id", "")) + " " + V.Str(e.Get("kind", "")) + "@" + V.Str(e.Get("body_part", ""))
                    + " sev=" + GdString.FormatFixed(e.GetFloat("severity", 0.0), 2) + " [" + flags + "]");
            }
            return texts;
        }

        public SelectableList List => _list;
        public string SummaryText => _summary.text;

        void Render()
        {
            List<GdDict> rows = WoundRows();
            var items = new List<SelectableList.Item>(rows.Count);
            foreach (GdDict e in rows)
            {
                double sev = e.GetFloat("severity", 0.0);
                Severity level = SeverityFor(sev);
                var flags = new List<string>();
                if (e.GetBool("bandaged", false)) flags.Add("bandaged");
                if (e.GetBool("treated", false)) flags.Add("treated");
                string part = V.Str(e.Get("body_part", ""));
                string text = GdString.Capitalize(V.Str(e.Get("kind", ""))) + (part.Length != 0 ? " · " + part : "");
                string detail = SeverityText.Word(level == Severity.Info ? Severity.None : level);
                detail = (detail.Length != 0 ? SeverityText.Symbol(level) + " " + detail + " · " : "")
                    + "severity " + GdString.FormatFixed(sev, 2)
                    + " · bleed " + GdString.FormatFixed(e.GetFloat("bleed_rate", 0.0), 2)
                    + (flags.Count > 0 ? " · " + string.Join(", ", flags) : " · untreated");
                if (_host != null)
                    detail += "\n" + TreatmentLine("Bandage", e.GetBool("can_bandage"), e.GetString("bandage_item_id"), e.GetString("bandage_reason"))
                        + " · " + TreatmentLine("Treat", e.GetBool("can_treat"), e.GetString("treat_item_id"), e.GetString("treat_reason"));
                items.Add(new SelectableList.Item
                {
                    Id = V.Str(e.Get("wound_id", "")),
                    Text = text,
                    Detail = detail,
                    Severity = level == Severity.Info ? Severity.None : level,
                });
            }
            _list.SetItems(items, _selected);
            if (_woundState == null)
            {
                _summary.text = "(no wound model)";
            }
            else
            {
                _summary.text = "Work speed ×" + GdString.FormatFixed(_woundState.WorkSpeedMultiplier(), 2)
                    + " · Bleed " + GdString.FormatFixed(_woundState.TotalBleedRate(), 2);
            }
            StatusText.Set(_status, _status.Length == 0 ? Severity.None : _statusDeny ? Severity.Caution : Severity.Success);
        }

        /// <summary>"Bandage: bandage_kit" when allowed, "Bandage: no bandage item" when refused.</summary>
        public static string TreatmentLine(string verb, bool allowed, string itemId, string reason) =>
            verb + ": " + (allowed ? itemId : ReasonText(reason));

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    MoveSelection(-1);
                    return true;
                case UiCommand.Down:
                    MoveSelection(1);
                    return true;
                case UiCommand.Accept:
                    TreatSelected();
                    return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
