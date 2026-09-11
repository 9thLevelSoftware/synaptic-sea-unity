// Ported from scripts/systems/scanner_state.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The SynapticSeaWorld members that ScannerState and TravelController use
    /// (<c>markers_in_range</c>, <c>player_position</c>, <c>set_player_position</c>, <c>mark_generated</c>).
    /// SynapticSeaWorld's C# port should implement it.
    /// </summary>
    public interface IMarkerWorld
    {
        /// <summary>Distinct markers within <paramref name="radius"/> of the player, sorted ascending by distance.</summary>
        IReadOnlyList<ShipMarker> MarkersInRange(double radius);

        Vec3 PlayerPosition { get; }

        void SetPlayerPosition(Vec3 pos);

        void MarkGenerated(string markerId);
    }

    /// <summary>
    /// Resolves which markers are visible and at what detail, gated by the ship's navigation/scanners systems and
    /// the player's scanner_operation skill. Pure logic: callers pass operational status as a plain dict.
    /// </summary>
    public class ScannerState
    {
        public const long MAX_DETAIL = 6;

        /// <summary>Spatial reach.</summary>
        public double RangeRadius = 250.0;

        /// <summary>Base detail from scanner hardware (upgradeable).</summary>
        public long HardwareDetail = 1;

        /// <summary>
        /// systems_ops: { "navigation": bool, "scanners": bool }. scanner_skill: 0..10.
        /// Returns { "detail_level": int, "markers": Array[Dictionary] }.
        /// </summary>
        public GdDict Scan(IMarkerWorld world, GdDict systemsOps, long scannerSkill)
        {
            // GDScript assert()s (debug builds halt).
            if (world == null)
                throw new ArgumentNullException(nameof(world), "ScannerState.scan: world must not be null");
            if (scannerSkill < 0)
                throw new ArgumentOutOfRangeException(nameof(scannerSkill), "ScannerState.scan: scanner_skill must be non-negative");
            systemsOps = systemsOps ?? new GdDict();
            if (!V.Bool(systemsOps.Get("navigation", false)))
                return new GdDict { { "detail_level", 0L }, { "markers", new GdArray() } };
            long detail = 1;
            if (V.Bool(systemsOps.Get("scanners", false)))
                detail = Math.Min(MAX_DETAIL, HardwareDetail + SkillBonus(scannerSkill));
            var views = new GdArray();
            foreach (ShipMarker m in world.MarkersInRange(RangeRadius))
                views.Add(MarkerView(m, world.PlayerPosition, detail));
            return new GdDict { { "detail_level", detail }, { "markers", views } };
        }

        /// <summary>Every 2 skill points -> +1 detail.</summary>
        static long SkillBonus(long skill) => skill / 2;

        static GdDict MarkerView(ShipMarker m, Vec3 playerPos, long detail)
        {
            var view = new GdDict
            {
                { "marker_id", m.MarkerId },
                { "position", m.Position.ToArray() },
                { "distance", (double)m.Position.DistanceTo(playerPos) },
                { "size_class", m.SizeClass },
            };
            if (detail >= 2)
                view["ship_type"] = m.ShipType;
            if (detail >= 3)
                view["condition"] = m.Condition;
            if (detail >= 4)
                view["predicted_status"] = PredictedStatus(m.Condition);
            if (detail >= 5)
                view["predicted_offline"] = PredictedOffline(m.Condition, m.SizeClass);
            if (detail >= 6)
                view["loot_hint"] = LootHint(m.SizeClass, m.Condition);
            return view;
        }

        static string PredictedStatus(long condition)
        {
            switch (condition)
            {
                case 0: return "systems nominal";
                case 1: return "systems degraded";
                default: return "systems critical";
            }
        }

        /// <summary>Deterministic guess of likely-offline systems from condition.</summary>
        static GdArray PredictedOffline(long condition, long sizeClass)
        {
            switch (condition)
            {
                case 0: return new GdArray();
                case 1: return GdArray.Of("scanners");
                default: return GdArray.Of("scanners", "navigation", "propulsion");
            }
        }

        static readonly string[] LootScales = { "meagre", "modest", "rich" };

        static string LootHint(long sizeClass, long condition)
        {
            string scale = LootScales[GdMath.Clampi(sizeClass, 0, 2)];
            string salvage = condition == 0 ? "intact" : "salvageable";
            return scale + " cache, " + salvage;
        }

        public GdDict GetSummary() =>
            new GdDict { { "range_radius", RangeRadius }, { "hardware_detail", HardwareDetail } };

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict d) || d.IsEmpty)
                return false;
            RangeRadius = V.F64(d.Get("range_radius", RangeRadius));
            HardwareDetail = V.I64(d.Get("hardware_detail", HardwareDetail));
            return true;
        }
    }
}
