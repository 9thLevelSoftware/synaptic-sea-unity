using System.Runtime.CompilerServices;

// The internal Godot String/file compat helpers (SurvivalCompat, ItemsCompat, ShipCompat, ...) are pinned by EditMode
// tests. The dotnet harness compiles Core and the tests into one assembly, so this only matters inside Unity.
[assembly: InternalsVisibleTo("SynapticSea.Tests.EditMode")]
