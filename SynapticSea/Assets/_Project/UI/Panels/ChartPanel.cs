// Ported from scripts/ui/chart_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Domain 10 (ADR-0045) web chart (LIVE inspection): a read-only, equivalent text list of the markers
    /// <see cref="WebChartState"/> recorded, plus an optional extraction route from <see cref="SeaGraph"/>. No interior
    /// rooms, no positions beyond what the chart recorded, and no travel action (travel stays on the scanner). The session
    /// opens it only when the player possesses a web_chart.
    /// </summary>
    public sealed class ChartPanel : SurfacePanel
    {
        public event Action PanelClosed;

        WebChartState _chart;
        SeaGraph _seaGraph;
        GdDict _routeSummary = new GdDict();
        bool _open;
        int _selected;

        readonly SelectableList _list;
        readonly Label _route;

        public ChartPanel() : base("WEB CHART", SurfaceTime.Live)
        {
            AddToClassList("ss-chart");
            _list = new SelectableList("chart", "No markers recorded.");
            Body.Add(_list);
            var routeSection = UiFactory.Box(UiClasses.Section);
            routeSection.Add(UiFactory.Text("Extraction route", UiClasses.SectionTitle));
            _route = UiFactory.Text("", UiClasses.LabelSecondary, UiClasses.LabelMono);
            routeSection.Add(_route);
            Body.Add(routeSection);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                Render();
            };
            Render();
        }

        public override string SurfaceId => "chart";

        public void Bind(WebChartState chartState)
        {
            _chart = chartState ?? throw new ArgumentNullException(nameof(chartState), "ChartPanel.bind: chart_state must not be null");
        }

        public void BindSeaGraph(SeaGraph seaGraph)
        {
            _seaGraph = seaGraph;
            Render();
        }

        public void SetRouteSummary(GdDict route)
        {
            _routeSummary = route?.DeepCopy() ?? new GdDict();
            Render();
        }

        public void RefreshExtractionRoute()
        {
            _routeSummary = _seaGraph == null ? new GdDict() : _seaGraph.RouteToExtraction();
            Render();
        }

        public List<string> GetRouteLines()
        {
            var lines = new List<string>();
            if (_routeSummary.IsEmpty) return lines;
            if (!_routeSummary.GetBool("ok", false))
            {
                lines.Add("Route: unavailable (" + V.Str(_routeSummary.Get("reason", "?")) + ")");
                return lines;
            }
            GdArray path = _routeSummary.GetArrayOrEmpty("path");
            lines.Add("Route to extraction (" + GdString.FormatInt(Math.Max(0, path.Count - 1)) + " hops)");
            lines.Add("  fuel=" + GdString.FormatFixed(_routeSummary.GetFloat("fuel", 0.0), 1)
                + " food=" + GdString.FormatFixed(_routeSummary.GetFloat("food", 0.0), 1)
                + " dist=" + GdString.FormatFixed(_routeSummary.GetFloat("distance", 0.0), 1));
            if (!path.IsEmpty)
            {
                var via = new List<string>();
                foreach (object n in path) via.Add(V.Str(n));
                lines.Add("  " + string.Join(" -> ", via));
            }
            return lines;
        }

        public bool IsOpen() => _open;

        public void Open()
        {
            _open = true;
            SetViewVisible(true);
            Refresh();
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

        public string GetStatus()
        {
            if (_chart == null) return "no chart";
            long count = _chart.GetKnownCount();
            string b = count == 0 ? "no markers recorded" : GdString.FormatInt(count) + " marker(s) recorded";
            if (_routeSummary.GetBool("ok", false)) b += " · route ready";
            return b;
        }

        public List<string> GetRowTexts()
        {
            var output = new List<string>();
            if (_chart == null) return output;
            foreach (object markerId in _chart.GetKnownMarkerIds()) output.Add(FormatRow(V.Str(markerId), _chart.GetEntry(V.Str(markerId))));
            return output;
        }

        static string FormatRow(string markerId, GdDict entry)
        {
            var parts = new List<string> { markerId, "sz=" + GdString.FormatInt(entry.GetInt("size_class", 0)) };
            if (entry.Has("ship_type")) parts.Add(V.Str(entry["ship_type"]));
            if (entry.Has("condition")) parts.Add("cond=" + GdString.FormatInt(V.I64(entry["condition"])));
            if (entry.Has("predicted_status")) parts.Add(V.Str(entry["predicted_status"]));
            if (entry.Has("loot_hint")) parts.Add(V.Str(entry["loot_hint"]));
            return string.Join(" · ", parts);
        }

        public SelectableList List => _list;
        public string RouteText => _route.text;

        void Render()
        {
            var items = new List<SelectableList.Item>();
            if (_chart != null)
            {
                foreach (object idV in _chart.GetKnownMarkerIds())
                {
                    string id = V.Str(idV);
                    GdDict entry = _chart.GetEntry(id);
                    items.Add(new SelectableList.Item
                    {
                        Id = id,
                        Chip = "D" + GdString.FormatInt(entry.GetInt("detail", 0)),
                        Text = id + (entry.Has("ship_type") ? " · " + V.Str(entry["ship_type"]) : ""),
                        Detail = FormatRow(id, entry),
                    });
                }
            }
            _list.SetItems(items, _selected);
            List<string> route = GetRouteLines();
            _route.text = route.Count == 0 ? "No route plotted." : string.Join("\n", route);
            bool unavailable = !_routeSummary.IsEmpty && !_routeSummary.GetBool("ok", false);
            SeverityText.Apply(_route, unavailable ? Severity.Caution : Severity.None);
            string status = GetStatus();
            StatusText.Set(status, _chart == null ? Severity.Caution : Severity.Info);
        }

        protected override bool OnCommand(UiCommand command)
        {
            if (command == UiCommand.Up || command == UiCommand.Down)
            {
                if (_list.Count == 0) return true;
                _selected = _list.StepFrom(_selected, command == UiCommand.Up ? -1 : 1);
                Render();
                return true;
            }
            return false;
        }

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
