// Ported from scripts/ui/wounds_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// PKG-D9d survivor wounds (LIVE inspection). Wound list + Bandage / Treat through the pure <see cref="WoundState"/>
    /// calls, with the Godot deny paths ("no wound", "bandage failed", "treat failed"). Spec: critical condition first
    /// (rows are ranked by severity, stable), body part shown only because the model supplies it, severity carries
    /// wording + symbol, and Bandage/Treat are focusable buttons (plus Submit = Treat) for keyboard and gamepad.
    /// </summary>
    public sealed class WoundsPanel : SurfacePanel
    {
        public event Action PanelClosed;
        public event Action<string, string> TreatmentApplied;

        WoundState _woundState;
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
            _woundState = woundState;
            Render();
        }

        public void SetAudioManager(IUiAudio audio) => _audio = audio;

        public bool IsOpen() => _open;

        public void Open()
        {
            _open = true;
            SetViewVisible(true);
            _selected = 0;
            _status = "";
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
                _status = "no wound";
                Render();
                PlayDeny();
                return false;
            }
            if (!_woundState.Bandage(wid))
            {
                _status = "bandage failed";
                Render();
                PlayDeny();
                return false;
            }
            _status = "bandaged " + wid;
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
                _status = "no wound";
                Render();
                PlayDeny();
                return false;
            }
            if (!_woundState.Treat(wid, severityReduce))
            {
                _status = "treat failed";
                Render();
                PlayDeny();
                return false;
            }
            _status = "treated " + wid;
            Reanchor(wid);
            TreatmentApplied?.Invoke(wid, "treat");
            Render();
            return true;
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
            foreach (object w in _woundState.Wounds)
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
            bool deny = _status == "no wound" || _status == "bandage failed" || _status == "treat failed";
            StatusText.Set(_status, _status.Length == 0 ? Severity.None : deny ? Severity.Caution : Severity.Success);
        }

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
