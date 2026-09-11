using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Contracts
{
    /// <summary>
    /// The pure-model contract every ported <c>RefCounted</c> system follows (Godot: configure / get_summary / apply_summary).
    /// Summaries are dictionary-shaped so save files, migrations, and Godot parity fixtures compare as trees.
    /// </summary>
    public interface ISimModel
    {
        void Configure(GdDict config);
        GdDict GetSummary();

        /// <summary>Returns false and leaves state unchanged when the summary is rejected.</summary>
        bool ApplySummary(GdDict summary);
    }

    /// <summary>Hazard/vitals family: <c>tick(delta, context)</c> with an ADR-0005 context dictionary.</summary>
    public interface ITickable
    {
        /// <summary>Returns true when state changed.</summary>
        bool Tick(double delta, GdDict context);
    }

    /// <summary>Ship-systems family: <c>advance(delta)</c>.</summary>
    public interface IAdvanceable
    {
        void Advance(double delta);
    }

    public interface IStatusLineProvider
    {
        IReadOnlyList<string> GetStatusLines();
    }

    /// <summary>ADR-0005 hazard contract: summaries carry a <c>hazard_kind</c> discriminator that apply validates.</summary>
    public interface IHazardState : ISimModel, IStatusLineProvider
    {
        string HazardKind { get; }
        bool IsPassabilityBlocked();
    }

    /// <summary>Per-ship snapshot unit (ShipRuntime, ShipInstance).</summary>
    public interface ISnapshotable
    {
        GdDict ToSnapshot();
        void FromSnapshot(GdDict data);
    }

    /// <summary>Models that persist themselves outside the run save (meta progression, unlocks, achievements).</summary>
    public interface IDiskPersisted
    {
        bool LoadFromDisk(Services.IStorage storage);
        bool SaveToDisk(Services.IStorage storage);
    }
}
