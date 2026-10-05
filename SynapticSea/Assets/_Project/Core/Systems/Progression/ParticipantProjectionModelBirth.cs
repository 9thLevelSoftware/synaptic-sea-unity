using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class InventoryState
    {
        internal static InventoryState PrepareProjectionBirth(InventoryState detached, out TrackedParticipantOwner owner, out object[] roots)
        {
            owner = TrackedParticipantOwner.PreparePrivateForest(new object[] { detached.Items, detached._definitions }, out roots);
            var model = new InventoryState(new GdDict()); model._tracking = owner;
            model._trackedValueItems = (GdDict)roots[0]; model._definitions = (GdDict)roots[1]; return model;
        }
    }
    public partial class PlayerProgressionState
    {
        internal static PlayerProgressionState PrepareProjectionBirth(PlayerProgressionState detached, out TrackedParticipantOwner owner, out object[] roots)
        {
            owner = TrackedParticipantOwner.PreparePrivateForest(new object[] {
                detached.Skills, detached.SkillXp, detached.SkillXpFractional, detached.CrossTraining, detached.BooksRead,
                detached._xpMultipliers, detached._skillCategory, detached._bookCatalog }, out roots);
            var model = new PlayerProgressionState(); model._tracking = owner; model._trackedValueClassId = detached.ClassId;
            model._trackedValueSkills = (GdDict)roots[0]; model._trackedValueSkillXp = (GdDict)roots[1];
            model._trackedValueSkillXpFractional = (GdDict)roots[2]; model._trackedValueCrossTraining = (GdDict)roots[3];
            model._trackedValueBooksRead = (GdDict)roots[4]; model._xpMultipliersValue = (GdDict)roots[5];
            model._skillCategory = (GdDict)roots[6]; model._bookCatalogValue = (GdDict)roots[7]; return model;
        }
    }
    public partial class TrainingEventBus
    {
        TrainingEventBus(TrackedParticipantOwner owner, GdDict actions, GdArray log)
        { _trackedOwner = owner; _actionsById = actions; _log = log; }
        internal static TrainingEventBus PrepareProjectionBirth(GdDict pinnedCatalog, out TrackedParticipantOwner owner, out object[] roots)
        {
            if (pinnedCatalog == null || pinnedCatalog.IsEmpty || !(pinnedCatalog.Get("training_actions") is GdArray))
                throw new ArgumentException("invalid_pinned_training_catalog");
            var detached = new TrainingEventBus();
            // Nonempty explicit catalog selects the ordinary pure normalization branch; no ambient load.
            if (!detached.Configure(pinnedCatalog)) throw new ArgumentException("invalid_pinned_training_catalog");
            owner = TrackedParticipantOwner.PreparePrivateForest(new object[] { detached._actionsById, detached._log }, out roots);
            return new TrainingEventBus(owner, (GdDict)roots[0], (GdArray)roots[1]);
        }
    }
}
