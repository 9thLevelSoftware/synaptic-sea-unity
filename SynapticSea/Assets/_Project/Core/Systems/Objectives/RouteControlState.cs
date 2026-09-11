// Ported from scripts/systems/route_control_state.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Runtime model for route access on the main playable slice. This model never reaches into the scene tree.
    /// RUNTIME: PlayableGeneratedShip owns the StaticBody3D gate nodes and applies scene consequences from this
    /// summary (nothing to split here: the .gd itself has no node code).
    /// </summary>
    public class RouteControlState : IStatusLineProvider
    {
        public GdDict GateRecords = new GdDict();
        public bool ExtractionUnlocked = false;

        public void ConfigureFromBlockedRoutes(GdArray routeGateIds)
        {
            GateRecords.Clear();
            ExtractionUnlocked = false;
            if (routeGateIds == null)
                return;
            foreach (object gateIdVariant in routeGateIds)
            {
                string gateId = V.Str(gateIdVariant);
                if (gateId.Length == 0)
                    continue;
                GateRecords[gateId] = NewRecord(gateId);
            }
        }

        static GdDict NewRecord(string gateId) =>
            new GdDict { { "id", gateId }, { "open", false }, { "required_system", "main_power_restored" } };

        public bool ApplyShipSystemsSummary(GdDict summary)
        {
            summary = summary ?? new GdDict();
            bool changed = false;
            bool shouldOpenPoweredGates = V.Bool(summary.Get("main_power_restored", false)) && V.Bool(summary.Get("blocked_routes_cleared", false));
            if (shouldOpenPoweredGates)
            {
                foreach (object gateId in new List<object>(GateRecords.Keys))
                {
                    var record = (GdDict)GateRecords[gateId];
                    if (!V.Bool(record.Get("open", false)))
                    {
                        record["open"] = true;
                        GateRecords[gateId] = record;
                        changed = true;
                    }
                }
            }
            if (V.Bool(summary.Get("extraction_unlocked", false)) && !ExtractionUnlocked)
            {
                ExtractionUnlocked = true;
                changed = true;
            }
            return changed;
        }

        public GdDict GetSummary()
        {
            var gateIds = new GdArray(GateRecords.Keys);
            GdSort.Sort(gateIds);
            // REQ-012: emit the per-gate open state as a nested dictionary so save/load can round-trip the route
            // gate topology exactly. Additive: consumers of the scalar fields are unaffected.
            var gateRecordsSnapshot = new GdDict();
            foreach (object gateId in gateIds)
            {
                var record = (GdDict)GateRecords[gateId];
                gateRecordsSnapshot[V.Str(gateId)] = new GdDict
                {
                    { "id", V.Str(record.Get("id", gateId)) },
                    { "open", V.Bool(record.Get("open", false)) },
                    { "required_system", V.Str(record.Get("required_system", "main_power_restored")) },
                };
            }
            return new GdDict
            {
                { "route_gate_count", (long)GateRecords.Count },
                { "active_blocker_count", ActiveBlockerCount() },
                { "opened_gate_count", OpenedGateCount() },
                { "powered_gates_open", GateRecords.Count > 0 && OpenedGateCount() == GateRecords.Count },
                { "extraction_unlocked", ExtractionUnlocked },
                { "gate_ids", gateIds },
                { "gate_records", gateRecordsSnapshot },
            };
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(ActiveBlockerCount() == 0 && GateRecords.Count > 0 ? "Routes: POWERED OPEN" : "Routes: BLOCKED");
            lines.Add(ExtractionUnlocked ? "Extraction: UNLOCKED" : "Extraction: LOCKED");
            return lines;
        }

        public bool IsGateOpen(string gateId)
        {
            if (!GateRecords.Has(gateId))
                return false;
            var record = (GdDict)GateRecords[gateId];
            return V.Bool(record.Get("open", false));
        }

        public bool IsExtractionUnlocked() => ExtractionUnlocked;

        /// <summary>
        /// REQ-012: restore this model from a summary matching GetSummary()'s shape. Unknown keys are ignored.
        /// Gate records are rebuilt from the gate_records block. Returns true if any field changed.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            bool newExtraction = V.Bool(summary.Get("extraction_unlocked", ExtractionUnlocked));
            if (newExtraction != ExtractionUnlocked)
            {
                ExtractionUnlocked = newExtraction;
                changed = true;
            }
            object recordsVariant = summary.Get("gate_records", null);
            if (recordsVariant is GdDict records)
            {
                foreach (object gateId in records.Keys)
                {
                    string gateIdStr = V.Str(gateId);
                    if (!(records[gateId] is GdDict record))
                        continue;
                    if (!GateRecords.Has(gateIdStr))
                    {
                        // Adopt the new gate id so the saved slice structure is preserved.
                        GateRecords[gateIdStr] = NewRecord(gateIdStr);
                    }
                    var existing = (GdDict)GateRecords[gateIdStr];
                    bool newOpen = V.Bool(record.Get("open", existing.Get("open", false)));
                    if (newOpen != V.Bool(existing.Get("open", false)))
                    {
                        existing["open"] = newOpen;
                        GateRecords[gateIdStr] = existing;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        long ActiveBlockerCount()
        {
            long count = 0;
            foreach (object gateId in GateRecords.Keys)
            {
                var record = (GdDict)GateRecords[gateId];
                if (!V.Bool(record.Get("open", false)))
                    count += 1;
            }
            return count;
        }

        long OpenedGateCount()
        {
            long count = 0;
            foreach (object gateId in GateRecords.Keys)
            {
                var record = (GdDict)GateRecords[gateId];
                if (V.Bool(record.Get("open", false)))
                    count += 1;
            }
            return count;
        }
    }
}
