using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        public readonly List<DeckTransition> DeckTransitions = new List<DeckTransition>();
        IShipSceneRoot _deckTransitionRoot;

        public void RefreshDeckTransitions()
        {
            IShipSceneRoot root = CurrentShip?.SceneRoot;
            if (root == _deckTransitionRoot) return;
            foreach (var old in DeckTransitions) { old.Free(); Events.RaiseInteractableDespawned(old); }
            DeckTransitions.Clear();
            _deckTransitionRoot = root;
            if (!(root is IShipLoaderView loader) || !root.IsValid) return;
            GdDict layout = loader.GetLayoutCopy();
            double cellSize = layout.GetFloat("cell_size", 4.0), deckHeight = layout.GetFloat("deck_height", 4.0);
            int index = 0;
            foreach (var row in layout.GetArrayOrEmpty("vertical_connections").OfType<GdDict>())
            {
                GdArray from = row.GetArrayOrEmpty("from_cell"), to = row.GetArrayOrEmpty("to_cell");
                if (from.Count < 3 || to.Count < 3 || V.I64(from[2]) == V.I64(to[2])) continue;
                Vec3 Position(GdArray cell) => new Vec3(V.F64(cell[0]) * cellSize,
                    V.F64(cell[2]) * deckHeight + PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, V.F64(cell[1]) * cellSize);
                AddLanding(from, to, "up");
                AddLanding(to, from, "down");
                index++;
                void AddLanding(GdArray source, GdArray target, string direction)
                {
                    var landing = new DeckTransition { Parent = root, LocalPosition = Position(source),
                        DestinationLocal = Position(target), DestinationDeck = V.I64(target[2]), SourceDeck = V.I64(source[2]),
                        ConnectionId = row.GetString("id"), ConnectionType = row.GetString("type"), InteractionRadius = 2.8,
                        NodeName = "DeckTransition_" + index + "_" + direction };
                    DeckTransitions.Add(landing);
                    Events.RaiseInteractableSpawned(landing);
                }
            }
        }

        internal bool TryDeckTransition(Vec3 player)
        {
            RefreshDeckTransitions();
            var landing = DeckTransitions.Where(d => d.IsValid && d.InReach(player))
                .OrderBy(d => d.GlobalPosition.DistanceSquaredTo(player)).FirstOrDefault();
            if (landing == null) return false;
            Vec3? clear = Deps.ResolveDeckLanding != null
                ? Deps.ResolveDeckLanding(landing.Destination, landing.Parent) : landing.Destination;
            if (!clear.HasValue) return false;
            TeleportPlayer(clear.Value);
            return true;
        }
    }
}
