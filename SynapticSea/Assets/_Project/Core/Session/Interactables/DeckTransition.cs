using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>One landing of an authored, bidirectional deck connection.</summary>
    public sealed class DeckTransition : SessionInteractable
    {
        public override string Kind => "deck_transition";
        public Vec3 DestinationLocal;
        public long DestinationDeck;
        public Vec3 Destination => Parent != null ? Parent.GlobalTransform * DestinationLocal : DestinationLocal;
        public bool InReach(Vec3 player) => IsPlayerInDirectRangeStrict(player)
            && System.Math.Abs(player.Y - GlobalPosition.Y) < 1.6;
    }
}
