// Ported from scripts/systems/title_save_query.gd @ 96ecb2b0
namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed SaveLoadService surface <see cref="TitleSaveQuery"/> calls
    /// (<c>has_slot(slot_id)</c> and <c>load_world()</c>, which returns a WorldSnapshot or null).
    /// The SaveLoadService port implements it.
    /// </summary>
    public interface ITitleSaveService
    {
        bool HasSlot(string slotId);
        WorldSnapshot LoadWorld();
    }

    /// <summary>
    /// ADR-0043: pure decision model for the title screen's Continue item. Continue is available when a world
    /// save exists, the world slot is not permadeath-frozen, and the save actually loads (migrates + validates).
    /// </summary>
    public static class TitleSaveQuery
    {
        public const string WorldSlotId = "world";

        /// <summary>
        /// <paramref name="service"/> / <paramref name="resolver"/> stay loosely typed (Godot typed them
        /// <c>Object</c> to avoid a preload cycle); anything not implementing the seam counts as missing.
        /// </summary>
        public static bool IsContinueAvailable(object service, object resolver)
        {
            var saveService = service as ITitleSaveService;
            var deathQuery = resolver as IDeathRecordQuery;
            if (saveService == null || deathQuery == null) return false;
            if (!saveService.HasSlot(WorldSlotId)) return false;
            if (deathQuery.HasDiedIn(WorldSlotId)) return false;
            // load_world() runs the migration + version + permadeath gates; null means request_load() would fail.
            return saveService.LoadWorld() != null;
        }
    }
}
