// Ported from scripts/procgen/topology_template.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Data class representing a ship topology template. Templates define the macro shape of a ship as named
    /// zones connected by a topology graph. Each zone has a role pool, room count range, position hints, and deck
    /// assignment. Templates are loaded from JSON and consumed by the RoomAssigner and CellLayoutEngine.
    /// </summary>
    public sealed class TopologyTemplate
    {
        public string Id = "";
        public string Description = "";

        /// <summary><c>Array[Dictionary]</c>.</summary>
        public List<GdDict> Zones = new List<GdDict>();

        /// <summary><c>Array[Dictionary]</c>.</summary>
        public List<GdDict> Connections = new List<GdDict>();

        public GdDict DeckConfig = new GdDict();

        public static TopologyTemplate FromDict(GdDict data)
        {
            var template = new TopologyTemplate();
            template.Id = V.Str(data.Get("id", ""));
            template.Description = V.Str(data.Get("description", ""));

            if (data.Get("zones", new GdArray()) is GdArray rawZones)
            {
                foreach (var zoneVariant in rawZones)
                {
                    if (!(zoneVariant is GdDict zone)) continue;
                    var parsedZone = new GdDict
                    {
                        { "id", V.Str(zone.Get("id", "")) },
                        { "role_pool", new GdArray() },
                        // Raw Variant: an int, a JSON double, or a [min, max] array.
                        { "count", zone.Get("count", 1L) },
                        { "position_hint", V.Str(zone.Get("position_hint", "center")) },
                        { "deck", V.I64(zone.Get("deck", 0L)) },
                        { "layout", V.Str(zone.Get("layout", "single")) },
                        { "attach_to", V.Str(zone.Get("attach_to", "")) },
                    };
                    if (zone.Get("role_pool", new GdArray()) is GdArray rawPool)
                    {
                        var pool = new GdArray();
                        foreach (var entry in rawPool) pool.Append(V.Str(entry));
                        parsedZone["role_pool"] = pool;
                    }
                    template.Zones.Add(parsedZone);
                }
            }

            if (data.Get("connections", new GdArray()) is GdArray rawConnections)
            {
                foreach (var connVariant in rawConnections)
                {
                    if (!(connVariant is GdDict conn)) continue;
                    template.Connections.Add(new GdDict
                    {
                        { "from", V.Str(conn.Get("from", "")) },
                        { "to", V.Str(conn.Get("to", "")) },
                        { "distribution", V.Str(conn.Get("distribution", "adjacent")) },
                    });
                }
            }

            if (data.Get("deck_config", new GdDict()) is GdDict rawDeck)
            {
                template.DeckConfig = new GdDict
                {
                    { "max_decks", V.I64(rawDeck.Get("max_decks", 1L)) },
                    { "vertical_transition_probability", V.F64(rawDeck.Get("vertical_transition_probability", 0.0)) },
                };
            }

            return template;
        }

        /// <summary>The zone dictionary itself (shared reference, like GDScript), or a new empty one.</summary>
        public GdDict GetZone(string zoneId)
        {
            foreach (var zone in Zones)
                if (V.Str(zone.Get("id", "")) == zoneId) return zone;
            return new GdDict();
        }

        public List<GdDict> GetZonesAttachedTo(string parentZoneId)
        {
            var result = new List<GdDict>();
            foreach (var zone in Zones)
                if (V.Str(zone.Get("attach_to", "")) == parentZoneId) result.Add(zone);
            return result;
        }
    }
}
