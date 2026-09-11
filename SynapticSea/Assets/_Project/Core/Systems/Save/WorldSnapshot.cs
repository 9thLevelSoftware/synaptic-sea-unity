// Ported from scripts/systems/world_snapshot.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Top-level world save: the SynapticSeaWorld summary, the home ship's RunSnapshot dict, per-derelict
    /// slices keyed by marker_id, the player's location and in-ship position. Pure data; geometry is never stored.
    /// </summary>
    public class WorldSnapshot
    {
        public const string WorldSliceVersion = "world-4";

        public GdDict WorldSummary = new GdDict();
        public GdDict HomeShip = new GdDict();                 // a RunSnapshot.ToDict()
        public GdDict MetaProgressionSummary = new GdDict();   // MetaProgressionState.ToDict()
        public GdDict UniqueItemSummary = new GdDict();        // UniqueItemState.get_summary()
        public GdArray HomeLootedContainers = new GdArray();   // home ship's searched loot-container ids
        public GdDict HomeShipInventory = new GdDict();        // home ship's ShipInventory.get_summary()
        public GdArray HomeShipCarts = new GdArray();          // home ship's [CartState.get_summary()...]
        public GdDict HomeBreachEnvironment = new GdDict();    // home ShipInstance breach environment only
        public GdDict PlayerEquipment = new GdDict();          // EquipmentState.get_summary()
        public GdDict VisitedShips = new GdDict();             // marker_id -> ShipInstance.get_summary()
        public string CurrentLocation = "";                    // "" = home ship, else marker_id
        // Live Persistent Ships Phase 1: monotonic in-run simulation clock (seconds).
        public double WorldTime = 0.0;
        public GdArray PlayerPositionInShip = GdArray.Of(0.0, 0.0, 0.0);
        public GdArray DockEdges = new GdArray();              // [{host, mobile, port_type, slot_index}]
        public string PilotedShipId = "";
        public string AboardShipId = "";
        public GdArray OpenedPorts = new GdArray();            // marker_ids with an opened dock barrier
        // run_id slot-ownership rework (ADR-0043 addendum).
        public string RunId = "";
        public string SliceVersion = "";
        /// <summary>Engine version stamp; the schema key stays <c>godot_version</c>.</summary>
        public string GodotVersion = "";
        public string SavedAt = "";

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "world_summary", WorldSummary.DeepCopy() },
                { "home_ship", HomeShip.DeepCopy() },
                { "meta_progression_summary", MetaProgressionSummary.DeepCopy() },
                { "unique_item_summary", UniqueItemSummary.DeepCopy() },
                { "home_looted_containers", HomeLootedContainers.ShallowCopy() },
                { "home_ship_inventory", HomeShipInventory.DeepCopy() },
                { "home_ship_carts", HomeShipCarts.DeepCopy() },
                { "home_breach_environment", HomeBreachEnvironment.DeepCopy() },
                { "player_equipment", PlayerEquipment.DeepCopy() },
                { "visited_ships", VisitedShips.DeepCopy() },
                { "current_location", CurrentLocation },
                { "world_time", WorldTime },
                { "player_position_in_ship", PlayerPositionInShip.ShallowCopy() },
                { "dock_edges", DockEdges.DeepCopy() },
                { "piloted_ship_id", PilotedShipId },
                { "aboard_ship_id", AboardShipId },
                { "opened_ports", OpenedPorts.ShallowCopy() },
                { "run_id", RunId },
                { "slice_version", SliceVersion },
                { "godot_version", GodotVersion },
                { "saved_at", SavedAt },
            };
        }

        /// <summary>
        /// Reconstructs a WorldSnapshot. Returns null when data is missing / not a dict / empty, or when either
        /// version marker does not match (ADR-0007/0012).
        /// </summary>
        public static WorldSnapshot FromDict(object data, string expectedWorldVersion, string expectedGodotVersion)
        {
            if (!(data is GdDict dict)) return null;
            if (dict.IsEmpty) return null;
            if (V.Str(dict.Get("slice_version", "")) != expectedWorldVersion) return null;
            if (V.Str(dict.Get("godot_version", "")) != expectedGodotVersion) return null;
            var ws = new WorldSnapshot();
            ws.WorldSummary = DeepCopyDict(dict.Get("world_summary", new GdDict()));
            ws.HomeShip = DeepCopyDict(dict.Get("home_ship", new GdDict()));
            ws.MetaProgressionSummary = DeepCopyDict(dict.Get("meta_progression_summary", new GdDict()));
            ws.UniqueItemSummary = DeepCopyDict(dict.Get("unique_item_summary", new GdDict()));
            object lootedVariant = dict.Get("home_looted_containers", new GdArray());
            if (lootedVariant is GdArray looted)
            {
                ws.HomeLootedContainers = new GdArray();
                foreach (var cid in looted) ws.HomeLootedContainers.Add(V.Str(cid));
            }
            ws.HomeShipInventory = DeepCopyDict(dict.Get("home_ship_inventory", new GdDict()));
            object hcVariant = dict.Get("home_ship_carts", new GdArray());
            ws.HomeShipCarts = hcVariant is GdArray hc ? hc.DeepCopy() : new GdArray();
            ws.HomeBreachEnvironment = DeepCopyDict(dict.Get("home_breach_environment", new GdDict()));
            ws.PlayerEquipment = DeepCopyDict(dict.Get("player_equipment", new GdDict()));
            ws.VisitedShips = DeepCopyDict(dict.Get("visited_ships", new GdDict()));
            ws.CurrentLocation = V.Str(dict.Get("current_location", ""));
            ws.WorldTime = V.F64(dict.Get("world_time", 0.0));
            object edgesV = dict.Get("dock_edges", new GdArray());
            if (edgesV is GdArray edges) ws.DockEdges = edges.DeepCopy();
            ws.PilotedShipId = V.Str(dict.Get("piloted_ship_id", ""));
            ws.AboardShipId = V.Str(dict.Get("aboard_ship_id", ""));
            object opV = dict.Get("opened_ports", new GdArray());
            if (opV is GdArray opened)
            {
                ws.OpenedPorts = new GdArray();
                foreach (var m in opened) ws.OpenedPorts.Add(V.Str(m));
            }
            // run_id slot-ownership rework: additive, no version bump; older saves default to "".
            ws.RunId = V.Str(dict.Get("run_id", ""));
            object pos = dict.Get("player_position_in_ship", GdArray.Of(0.0, 0.0, 0.0));
            if (pos is GdArray pa && pa.Count >= 3)
                ws.PlayerPositionInShip = GdArray.Of(V.F64(pa[0]), V.F64(pa[1]), V.F64(pa[2]));
            ws.SliceVersion = V.Str(dict.Get("slice_version", ""));
            ws.GodotVersion = V.Str(dict.Get("godot_version", ""));
            ws.SavedAt = V.Str(dict.Get("saved_at", ""));
            return ws;
        }

        static GdDict DeepCopyDict(object src)
        {
            if (!(src is GdDict d)) return new GdDict();
            return d.DeepCopy();
        }
    }
}
