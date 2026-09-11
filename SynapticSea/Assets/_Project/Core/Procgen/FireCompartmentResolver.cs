// Ported from scripts/procgen/fire_compartment_resolver.gd @ 96ecb2b0
using System.Collections;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    public static class FireCompartmentResolver
    {
        /// <summary>Single room-role vocabulary shared by the builder preview and boarded runtime.</summary>
        public static readonly GdDict COMPARTMENT_FOR_ROLE = new GdDict
        {
            { "bridge", "bridge" },
            { "cockpit", "bridge" },
            { "engineering", "engineering" },
            { "reactor", "engineering" },
            { "engine_bay", "engineering" },
            { "hydroponics", "hydroponics" },
            { "cargo", "cargo" },
            { "storage", "cargo" },
        };

        public static string FromToken(string raw)
        {
            string token = ProcgenCompat.StripEdges(raw);
            if (token.Length == 0) return "";
            foreach (var value in COMPARTMENT_FOR_ROLE.Values)
                if (V.VariantEquals(value, token)) return token;
            return V.Str(COMPARTMENT_FOR_ROLE.Get(token, ""));
        }

        /// <param name="layoutSources">GDScript <c>Array</c> of layout dictionaries (non-dictionaries are skipped).</param>
        public static string FromRoomId(string roomId, IEnumerable layoutSources)
        {
            string token = ProcgenCompat.StripEdges(roomId);
            if (token.Length == 0) return "";
            if (layoutSources == null) return "";
            foreach (var layoutVariant in layoutSources)
            {
                if (!(layoutVariant is GdDict layout)) continue;
                if (!(layout.Get("rooms", new GdArray()) is GdArray rooms)) continue;
                foreach (var roomVariant in rooms)
                {
                    if (!(roomVariant is GdDict room)) continue;
                    if (V.Str(room.Get("id", "")) != token) continue;
                    return FromToken(V.Str(room.Get("room_role", room.Get("role", ""))));
                }
            }
            return "";
        }

        public static string FromZone(GdDict zone, IEnumerable layoutSources)
        {
            foreach (string candidateKey in new[] { "compartment_id", "to_room", "from_room" })
            {
                string candidate = V.Str(zone.Get(candidateKey, ""));
                string mapped = FromToken(candidate);
                if (mapped.Length == 0) mapped = FromRoomId(candidate, layoutSources);
                if (mapped.Length != 0) return mapped;
            }
            return "";
        }
    }
}
