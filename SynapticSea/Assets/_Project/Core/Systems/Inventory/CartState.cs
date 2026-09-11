// Ported from scripts/systems/cart_state.gd @ 96ecb2b0

using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// A pushable cart: a mobile container wrapping a ShipInventory. Its contents are never added to the player's
    /// personal encumbrance (they live in the cart hold); a cart "removes" weight from the player whereas a worn bag
    /// only raises the cap. Pure model; never touches the scene tree. Round-trips via GetSummary/ApplySummary.
    /// </summary>
    public class CartState
    {
        public const double MAX_WEIGHT_DEFAULT = 200.0;
        public const double PUSH_SPEED_MULTIPLIER_DEFAULT = 0.7;

        public string CartId = "";
        public string ParkedShipId = "";
        public Vec3 ParkedPosition = Vec3.Zero;
        public double PushSpeedMultiplier = PUSH_SPEED_MULTIPLIER_DEFAULT;
        ShipInventory _hold;

        public CartState()
        {
            _hold = ShipInventory.Create(MAX_WEIGHT_DEFAULT);
        }

        /// <summary>The GDScript load()-self-reference factory.</summary>
        public static CartState Create(string pCartId = "", double pMaxWeight = MAX_WEIGHT_DEFAULT)
        {
            var inst = new CartState();
            inst.CartId = pCartId;
            inst._hold = ShipInventory.Create(pMaxWeight);
            return inst;
        }

        public ShipInventory GetHold() => _hold;

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "cart_id", CartId },
                { "parked_ship_id", ParkedShipId },
                { "parked_position", ParkedPosition.ToArray() },
                { "push_speed_multiplier", PushSpeedMultiplier },
                { "hold", _hold.GetSummary() },
            };
        }

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict d) || d.IsEmpty)
                return false;
            CartId = V.Str(d.Get("cart_id", CartId));
            ParkedShipId = V.Str(d.Get("parked_ship_id", ParkedShipId));
            object p = d.Get("parked_position", null);
            if (p is GdArray pa && pa.Count == 3)
                ParkedPosition = new Vec3(V.F64(pa[0]), V.F64(pa[1]), V.F64(pa[2]));
            if (d.Has("push_speed_multiplier"))
                PushSpeedMultiplier = V.F64(d["push_speed_multiplier"]);
            object holdSummary = d.Get("hold", null);
            if (holdSummary is GdDict holdDict)
                _hold.ApplySummary(holdDict);
            return true;
        }
    }
}
