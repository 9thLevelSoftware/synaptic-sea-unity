// Ported from scripts/ui/scanner_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>The coordinator seam the scanner panel calls (Godot duck-typed PlayableGeneratedShip).</summary>
    public interface IScannerHost
    {
        /// <summary>{detail_level, markers:[view dicts]} (ScannerState.Scan through the ship's systems and skill).</summary>
        GdDict Scan();

        /// <summary>{success, reason, ship}.</summary>
        GdDict TravelToMarkerId(string markerId);

        /// <summary>Stream D training event, once per deliberate open.</summary>
        void EmitTrainingEvent(string eventId, string targetId);

        /// <summary>UI audio for open/deny cues (null allowed).</summary>
        IUiAudio Audio { get; }
    }

    /// <summary>
    /// Reference <see cref="IScannerHost"/> over the Core models, mirroring PlayableGeneratedShip.scan() /
    /// travel_to_marker_id(): a possessed web chart auto-records every scan (ADR-0045 Source B); travel resolves the
    /// marker from the in-range set and hands it to the session's travel function.
    /// </summary>
    public sealed class ScannerHost : IScannerHost
    {
        readonly ScannerState _scanner;
        readonly IMarkerWorld _world;
        readonly Func<GdDict> _systemsOps;
        readonly Func<long> _scannerSkill;
        readonly Func<ShipMarker, GdDict> _travel;
        readonly WebChartState _chart;
        readonly Func<bool> _hasWebChart;

        public IUiAudio Audio { get; set; }
        public Action<string, string> TrainingEvent;

        public ScannerHost(ScannerState scanner, IMarkerWorld world, Func<GdDict> systemsOps, Func<long> scannerSkill,
            Func<ShipMarker, GdDict> travel, WebChartState chart = null, Func<bool> hasWebChart = null)
        {
            _scanner = scanner;
            _world = world;
            _systemsOps = systemsOps;
            _scannerSkill = scannerSkill;
            _travel = travel;
            _chart = chart;
            _hasWebChart = hasWebChart;
        }

        public GdDict Scan()
        {
            if (_world == null || _scanner == null) return new GdDict { { "detail_level", 0L }, { "markers", new GdArray() } };
            GdDict result = _scanner.Scan(_world, _systemsOps?.Invoke() ?? new GdDict(), _scannerSkill?.Invoke() ?? 0);
            if (_chart != null && _hasWebChart != null && _hasWebChart())
                _chart.RecordViews(result.GetArrayOrEmpty("markers"), result.GetInt("detail_level", 0));
            return result;
        }

        public GdDict TravelToMarkerId(string markerId)
        {
            if (_world == null || _scanner == null) return new GdDict { { "success", false }, { "reason", "not_ready" }, { "ship", null } };
            foreach (ShipMarker m in _world.MarkersInRange(_scanner.RangeRadius))
            {
                if (m.MarkerId != markerId) continue;
                if (_travel == null) return new GdDict { { "success", false }, { "reason", "not_ready" }, { "ship", null } };
                return _travel(m);
            }
            return new GdDict { { "success", false }, { "reason", "unknown_marker" }, { "ship", null } };
        }

        public void EmitTrainingEvent(string eventId, string targetId) => TrainingEvent?.Invoke(eventId, targetId);
    }

    /// <summary>
    /// Scanner / travel (LIVE inspection). Backed selectable contact list + detail + Travel confirmation (spec
    /// "Scanner/travel"). Detail distinguishes known data from fields the scan detail level has not revealed, and
    /// never shows more than the scan view carries. Deny paths match Godot: "no scanner", "no signal", "no target",
    /// "no travel", or the travel rejection reason.
    /// </summary>
    public sealed class ScannerPanel : SurfacePanel
    {
        public event Action PanelClosed;
        /// <summary>Raised with the host's result dict after every confirm attempt.</summary>
        public event Action<GdDict> TravelResolved;

        IScannerHost _host;
        GdArray _markers = new GdArray();
        long _detailLevel;
        int _selected;
        string _status = "";
        bool _open;

        readonly SelectableList _list;
        readonly Label _detail;
        readonly Button _travel;

        public ScannerPanel() : base("SCANNER", SurfaceTime.Live)
        {
            AddToClassList("ss-scanner");
            var columns = UiFactory.Box(UiClasses.Columns);
            var listPane = UiFactory.Box(UiClasses.Pane);
            _list = new SelectableList("contact", "No contacts.") { Wrap = true };
            listPane.Add(_list);
            var detailPane = UiFactory.Box(UiClasses.Pane, UiClasses.Detail);
            _detail = UiFactory.Text("", UiClasses.LabelSecondary);
            detailPane.Add(_detail);
            columns.Add(listPane);
            columns.Add(detailPane);
            Body.Add(columns);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            _travel = UiFactory.Button("Travel", () => ConfirmSelection(), "act:travel");
            tools.Add(_travel);
            tools.Add(UiFactory.Button("Rescan", () => Refresh(), "act:rescan"));
            Body.Add(tools);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
            _list.ActivateRequested += i =>
            {
                _selected = i;
                ConfirmSelection();
            };
            Render();
        }

        public override string SurfaceId => "scanner";

        public void Bind(IScannerHost host) => _host = host;

        public bool IsOpen() => _open;

        public void Open()
        {
            _open = true;
            SetViewVisible(true);
            _selected = 0;
            Refresh();
            _host?.EmitTrainingEvent("scan_derelict", "scanner_panel");
            _host?.Audio?.PlaySfx(AudioEventSeam.UI_PANEL_OPEN);
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

        public void Refresh()
        {
            if (_host == null)
            {
                _markers = new GdArray();
                _detailLevel = 0;
                _status = "no scanner";
                Render();
                return;
            }
            GdDict result = _host.Scan();
            _markers = result.GetArrayOrEmpty("markers");
            _detailLevel = result.GetInt("detail_level", 0);
            _status = _markers.IsEmpty ? "no signal" : GdString.FormatInt(_markers.Count) + " contact(s)";
            _selected = (int)GdMath.Clampi(_selected, 0, Math.Max(0, _markers.Count - 1));
            Render();
        }

        public void MoveSelection(int dir)
        {
            if (_markers.IsEmpty) return;
            _selected = (int)GdMath.Wrapi(_selected + dir, 0, _markers.Count);
            Render();
        }

        public int GetSelectedIndex() => _selected;
        public string GetStatus() => _status;

        public GdDict ConfirmSelection()
        {
            GdDict result;
            if (_markers.IsEmpty)
            {
                _status = "no target";
                Render();
                PlayDeny();
                result = new GdDict { { "success", false }, { "reason", "no_target" }, { "ship", null } };
                TravelResolved?.Invoke(result);
                return result;
            }
            if (_host == null)
            {
                _status = "no travel";
                Render();
                PlayDeny();
                result = new GdDict { { "success", false }, { "reason", "not_ready" }, { "ship", null } };
                TravelResolved?.Invoke(result);
                return result;
            }
            string markerId = V.Str(((GdDict)_markers[_selected]).Get("marker_id", ""));
            result = _host.TravelToMarkerId(markerId);
            if (result.GetBool("success", false))
            {
                Close();
            }
            else
            {
                _status = V.Str(result.Get("reason", "rejected"));
                Render();
            }
            TravelResolved?.Invoke(result);
            return result;
        }

        void PlayDeny() => _host?.Audio?.PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        public List<string> GetRowTexts()
        {
            var output = new List<string>();
            foreach (object view in _markers) output.Add(FormatRow((GdDict)view));
            return output;
        }

        /// <summary>The Godot row text: "id · d=120 · sz=2 · type · cond=1 · status · hint".</summary>
        public static string FormatRow(GdDict view)
        {
            var parts = new List<string> { V.Str(view.Get("marker_id", "?")) };
            parts.Add("d=" + GdString.FormatFixed(view.GetFloat("distance", 0.0), 0));
            parts.Add("sz=" + GdString.FormatInt(view.GetInt("size_class", 0)));
            if (view.Has("ship_type")) parts.Add(V.Str(view["ship_type"]));
            if (view.Has("condition")) parts.Add("cond=" + GdString.FormatInt(V.I64(view["condition"])));
            if (view.Has("predicted_status")) parts.Add(V.Str(view["predicted_status"]));
            if (view.Has("loot_hint")) parts.Add(V.Str(view["loot_hint"]));
            return string.Join(" · ", parts);
        }

        public SelectableList List => _list;
        public string DetailText => _detail.text;

        void Render()
        {
            var items = new List<SelectableList.Item>();
            foreach (object v in _markers)
            {
                var view = (GdDict)v;
                items.Add(new SelectableList.Item
                {
                    Id = V.Str(view.Get("marker_id", "?")),
                    Chip = GdString.FormatFixed(view.GetFloat("distance", 0.0), 0) + "m",
                    Text = V.Str(view.Get("marker_id", "?")) + (view.Has("ship_type") ? " · " + V.Str(view["ship_type"]) : ""),
                    Detail = "size " + GdString.FormatInt(view.GetInt("size_class", 0))
                        + (view.Has("predicted_status") ? " · " + V.Str(view["predicted_status"]) : ""),
                });
            }
            _list.SetItems(items, _selected);
            _detail.text = DetailFor(_markers.IsEmpty ? null : (GdDict)_markers[_selected]);
            bool deny = _status == "no scanner" || _status == "no signal" || _status == "no target" || _status == "no travel"
                || (_status.Length != 0 && !GdString.EndsWith(_status, "contact(s)"));
            StatusText.Set(_status, _status.Length == 0 ? Severity.None : deny ? Severity.Caution : Severity.Info);
            _travel.SetEnabled(!_markers.IsEmpty);
        }

        string DetailFor(GdDict view)
        {
            if (view == null) return _host == null ? "Scanner unavailable." : "No contacts in range.";
            const string unknown = "unknown (needs a stronger scan)";
            var lines = new List<string>
            {
                "Contact " + V.Str(view.Get("marker_id", "?")),
                "Scan detail " + GdString.FormatInt(_detailLevel) + " / " + GdString.FormatInt(ScannerState.MAX_DETAIL),
                "Distance " + GdString.FormatFixed(view.GetFloat("distance", 0.0), 0),
                "Size class " + GdString.FormatInt(view.GetInt("size_class", 0)),
                "Ship type " + (view.Has("ship_type") ? V.Str(view["ship_type"]) : unknown),
                "Condition " + (view.Has("condition") ? GdString.FormatInt(V.I64(view["condition"])) : unknown),
                "Predicted " + (view.Has("predicted_status") ? V.Str(view["predicted_status"]) : unknown),
            };
            if (view.Has("predicted_offline"))
            {
                var offline = new List<string>();
                foreach (object s in view.GetArrayOrEmpty("predicted_offline")) offline.Add(V.Str(s));
                lines.Add("Likely offline " + (offline.Count == 0 ? "none" : string.Join(", ", offline)));
            }
            else
            {
                lines.Add("Likely offline " + unknown);
            }
            lines.Add("Salvage " + (view.Has("loot_hint") ? V.Str(view["loot_hint"]) : unknown));
            return string.Join("\n", lines);
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
                    ConfirmSelection();
                    return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
