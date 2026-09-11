// Ported from scripts/systems/ship_access_state.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Per-ship ownership + access list. Pure data (no scene tree). The multiplayer forward seam: one local player
    /// this cycle, but owner_id + access_ids + the grant/revoke methods generalize to N players.
    /// Persisted as a ship-summary sub-dict.
    /// </summary>
    public class ShipAccessState
    {
        public string OwnerId = "";
        public List<string> AccessIds = new List<string>();

        public static ShipAccessState Create() => new ShipAccessState();

        /// <summary>
        /// Claims an unowned ship for player_id (sets owner + grants access). Returns whether player_id now owns it:
        /// true if it just claimed or already owned it, false if a different player already owns it.
        /// </summary>
        public bool Claim(string playerId)
        {
            if (playerId == "")
                return false;
            if (OwnerId == "")
            {
                OwnerId = playerId;
                AddAccess(playerId);
                return true;
            }
            return OwnerId == playerId;
        }

        public void Grant(string playerId)
        {
            if (playerId != "")
                AddAccess(playerId);
        }

        public void Revoke(string playerId)
        {
            if (playerId == OwnerId)
                return; // the owner always retains access
            AccessIds.Remove(playerId);
        }

        public bool HasAccess(string playerId) => playerId != "" && AccessIds.Contains(playerId);

        void AddAccess(string playerId)
        {
            if (!AccessIds.Contains(playerId))
                AccessIds.Add(playerId);
        }

        public GdDict GetSummary() =>
            new GdDict { { "owner_id", OwnerId }, { "access_ids", ShipCompat.ToGdArray(AccessIds) } };

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict d))
                return false;
            OwnerId = V.Str(d.Get("owner_id", ""));
            AccessIds.Clear();
            object raw = d.Get("access_ids", new GdArray());
            if (raw is GdArray rawArr)
            {
                foreach (object a in rawArr)
                    AddAccess(V.Str(a));
            }
            return true;
        }
    }
}
