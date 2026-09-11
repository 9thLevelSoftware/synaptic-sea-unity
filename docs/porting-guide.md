# GDScript → C# porting guide

This guide is the contract for porting `D:\the-synaptic-sea` (Godot 4.7, `main` @ `96ecb2b0`) into the Unity project at `F:\synaptic-sea\SynapticSea`. It refines `docs/unity-port-plan.md` with decisions made while building the kernel. Read it fully before porting a file.

## Where code goes

| Godot source | C# location | Namespace |
|---|---|---|
| `scripts/systems/<file>.gd` (pure `RefCounted`) | `Assets/_Project/Core/Systems/<Domain>/<PascalName>.cs` | `SynapticSea.Core.Systems` |
| `scripts/audio/audio_event_seam.gd`, `audio_log.gd` | `Assets/_Project/Core/Systems/Audio/` | `SynapticSea.Core.Systems` |
| `scripts/procgen/<file>.gd` (pure) | `Assets/_Project/Core/Procgen/<PascalName>.cs` | `SynapticSea.Core.Procgen` |
| Anything that creates or walks scene nodes | `Assets/_Project/Runtime/...` (later phase) | `SynapticSea.Runtime` |
| EditMode tests | `Assets/_Project/Tests/EditMode/Systems/<Domain>/` or `.../Procgen/` | `SynapticSea.Tests.Systems` / `SynapticSea.Tests.Procgen` |

- **Procgen lives inside the Core assembly.** `component_placement_state` and `ship_instance` depend on procgen files, so a separate Procgen assembly would create a reference cycle.
- **One namespace per layer**, not per domain. Every system file is in `SynapticSea.Core.Systems`, so cross-references need no extra `using`. Domains are only folders.
- **Class name = the GDScript `class_name`**, for example `OxygenState` or `LootRoller`. If a file has no `class_name`, PascalCase the file name.
- **First line of every ported file:** `// Ported from scripts/systems/oxygen_state.gd @ 96ecb2b0`.

## Kernel API (already written; do not modify without asking)

All of it is in `Assets/_Project/Core`. Read the source; this is a summary.

| GDScript | C# |
|---|---|
| `Dictionary` | `GdDict` (insertion-ordered, Variant keys). `d["k"] = v`, `d.Get(k, fallback)`, `d.Has(k)`, `d.Erase(k)`, `d.Keys`, `d.Values`, `d.Merge(o, overwrite)`, `d.DeepCopy()` (`duplicate(true)`), `d.ShallowCopy()` |
| `Array` | `GdArray`. `Add`/`Append`, `Insert`, `RemoveAt`, `Remove` (`erase`), `Contains` (`has`), `IndexOf` (`find`), `PopBack`, `PopFront`, `Front`, `Back`, `AppendArray`, `SortCustom(less)`, `DeepCopy`, `ShallowCopy`, `GdArray.Of(...)` |
| `int(d.get("k", 0))` | `d.GetInt("k", 0)`. Also `GetFloat`, `GetString`, `GetBool`, `GetDict`, `GetArray`, `GetDictOrEmpty`, `GetArrayOrEmpty` |
| `int(x)` `float(x)` `str(x)` `bool(x)` | `V.I64(x)` `V.F64(x)` `V.Str(x)` `V.Bool(x)` |
| `if x:` on a Variant | `V.Truthy(x)` |
| `typeof(x) == TYPE_DICTIONARY` / `x is Dictionary` | `x is GdDict` or `V.Dict(x) != null` |
| `typeof(x) == TYPE_ARRAY` | `x is GdArray` |
| `x is int` / `x is float` / numeric | `x is long` / `x is double` / `V.IsNumber(x)` |
| `a == b` between Variants | `V.VariantEquals(a, b)` (int 1 equals float 1.0; containers compare deeply) |
| `Array.sort()` / `sort_custom(f)` | `GdSort.Sort(list)` / `GdSort.SortCustom(list, less)`. **Never `List.Sort`**: Godot's introsort is unstable and tie order must match |
| `Vector3` | `Vec3` (float32, Godot frame). `Vec3.FromArray(v)`, `.ToArray()` gives `[x, y, z]` doubles |
| `Vector2i` | `Vec2i`. `Vec2i.FromArray(v)`, `.ToArray()` |
| `round` `floor` `ceil` `clampf` `clampi` `lerpf` `move_toward` `is_equal_approx` `posmod` `fposmod` `snapped` `wrapf` `wrapi` `signf` | `GdMath.Round` (half away from zero; **never `Math.Round`**), `GdMath.Floor`, and so on |
| `min`/`max`/`abs` | `Math.Min`/`Math.Max`/`Math.Abs` with the same operand types GDScript had |
| `RandomNumberGenerator.new(); rng.seed = s` | `var rng = GodotRandom.FromSeed(s);` then `rng.Randi()`, `rng.Randf()`, `rng.RandiRange(a, b)`, `rng.RandfRange(a, b)`, `rng.Randfn(m, d)`, `rng.Seed`, `rng.State`, `rng.RandWeighted(float[])` |
| global `randi()` `randf()` `randi_range()` `randf_range()` `randfn()` `seed()` | `GodotGlobalRandom.*`. These use different float algorithms from the RNG class; keep whichever the source used |
| `s.hash()` / `hash(s)` / `hash(int)` | `GodotHash.StringHash(s)` / `GodotHash.Hash(v)` / `GodotHash.IntHash(i)` |
| `JSON.parse_string(text)` | `GdJson.ParseString(text)` (null on failure). **Every JSON number parses as `double`**, exactly like Godot |
| `JSON.stringify(v, indent)` | `GdJson.Stringify(v, indent)` (keys sorted like Godot) |
| `FileAccess` read of `res://data/...json` | `CatalogRegistry.LoadDict("res://data/...")` (returns a deep copy; null when missing) |
| `FileAccess` / `DirAccess` on `user://` | an injected `IStorage` (constructor parameter or property). Fall back to `CoreServices.UserStorage` only in static helpers |
| `Time.get_unix_time_from_system()` etc. | an injected `IClock`, falling back to `CoreServices.Clock` |
| `push_warning` / `push_error` / `print` | `CoreServices.Log.Warning/Error/Info` (or an injected `ILog`) |
| `Engine.get_version_info()["string"]` | `CoreServices.Engine.VersionString` |
| `preload("res://scripts/...")` | a direct type reference |
| `Callable` properties | `Action<...>` / `Func<...>` fields; `is_valid()` becomes a null check |
| `signal x(a, b)` + `emit_signal` | `public event Action<A, B> X;` + `X?.Invoke(a, b)` |
| `has_method("m")` / `.call("m", ...)` | an interface, or `as ISomething` plus a null check where the call is genuinely optional. Put small shared interfaces next to the implementing class |
| `StringName` / `&"x"` | `string` |
| `s.find/contains/begins_with/strip_edges/trim_prefix/is_valid_int/capitalize/path_join/split`, `"%.2f" % v`, `"%d" % i`, `sha256_text` | `GdString.*` (Godot semantics; never `string.Trim`, `IndexOf("")`, culture formatting). Do **not** add new `*Compat` helpers for these |
| `PackedStringArray` | `List<string>` when internal; `GdArray` when it reaches a summary |

## Cross-wave interfaces

Wave 1 introduced interfaces that later ports must implement instead of inventing new ones. Search `Core/Systems` for `interface I` before adding one. Known obligations:
- `EffectDispatcher` targets: `IVitalsTarget`, `ISanityTarget`, `IRadiationTarget`, `IBodyTemperatureTarget`, `IStatusEffectsTarget`.
- `CargoTransfer`: `ICargoStore`, `ICargoPlayer`, `ICargoHold`. `FoodTravelPlanner`: `ITravelRangeSource`, `IFoodRegistry`, `IFoodLookup`.
- `ModuleDamageRouter`: `IModuleIntegrityMap`. `DockingManager`: `IShipSceneRoot`, `IDockableShip`. `ScannerState`/`TravelController`: `IMarkerWorld`, `IShipGenerator`.
- `SkillEffectsResolver`: `ISkillLevelSource`. `TitleSaveQuery`: `ITitleSaveService`, `IDeathRecordQuery`. `SettingsState`: `IAccessibilitySettingsSink`. `HubUpgradeState`: `IHubUpgradeWallet`. `DifficultyProfile`: `IModifierSource`.
- Wave 2: `IItemAcceptor` (InventoryState.cs; CraftingState's `can_accept` check), `ConsumableState.ISpoilageFoodSource` (`GetFood(string) -> FoodState`; SpoilageState implements it), `ComponentMountResolver.IComponentPlacement` (`Dismount`, `Mount`; ComponentPlacementState implements it). `InventoryState` implements `ICargoPlayer`/`ICargoHold`; `ShipInventory` implements `ICargoHold`. `LootDistribution.Roll(table, seed, tables, context)` plus `RollWithUniqueState(...)`.
- Wave 2 combat: `DamagePipeline.IDamageVitalsTarget { double Health { get; set; } }` (VitalsState implements it); `StatusEffectsState` implements `EffectDispatcher.IStatusEffectsTarget`. `PlayerProgressionState` implements `ISkillLevelSource`; `ModuleIntegrityMap` implements `IModuleIntegrityMap`.
- Wave 3 (Runtime implements): `IModuleSceneView` + `IShipModuleScene` (ModuleIntegrityConsequences/IntegrityVisualResolver), `IShipInteriorView` (ShipInstance). Core implements: `ShipRuntime.IComponentManifestModel` (ComponentPlacementState), `SkillTreeState.IBookReadSource` (PlayerProgressionState), `SkillTreeState.ICodexEntrySource` (MetaProgressionState). `ShipRuntime.Configure(ShipRuntimeOptions)`. `ShipInstance` implements `IDockableShip`; `SynapticSeaWorld` implements `IMarkerWorld`; `SaveLoadService` implements `ITitleSaveService`.
- `PhaseTimer` exposes `CurrentPhaseValue` (not `phase`); `ShipBlueprint` exposes `ShipSize`, `ShipCondition`, `SeedValue`.
- Procgen stages (Wave 2): `RoomAssigner.AssignWithSelector` takes the variant selector as `object` (`RoomVariantSelector` or `IRoomVariantPicker`); `EncounterInjector.Inject` takes `IModifierSource` biome/difficulty (normally `BiomeProfile`/`DifficultyProfile`); `StructuralPlacer.PlaceStructure` returns a `StructuralPlacer.Placement` record tree (RUNTIME builder input) instead of a `Node3D`.

## Numeric rules

- **GDScript `float` is a 64-bit double.** Model state uses `double`. Only `Vec3` is float32, matching Godot's `Vector3`.
- **GDScript `int` is int64.** Use `long` for any value that is a seed, hash, epoch, counter that can grow, or anything passed through bit operations or `%` of hashes. `int` is fine for small bounded counts and indices. When unsure, use `long`.
- Integer `/` and `%` truncate toward zero in both languages. Float `%` is `fmod`.
- Integer arithmetic wraps in GDScript. Wrap it in C# with `unchecked(...)` where overflow is possible, as with hash mixing and `seed * 1103515245`.
- **Ints versus floats in summaries:** a value written from an `int` field stays `long`, and one written from a `float` field stays `double`. This preserves JSON output (`17` versus `17.0`). Anything read back from JSON is always `double`, so coerce on read with `V.I64` or `V.F64`, exactly where the GDScript did `int()` or `float()`.

## Contracts

- Implement `ISimModel` for models with `configure` / `get_summary` / `apply_summary`, and `ITickable` / `IAdvanceable` / `IHazardState` where the shape matches. If a model's method signature differs (for example `tick(delta, bool)`), keep the GDScript signature as the public method and add the interface only when it fits naturally. **Behavior parity beats interface purity.**
- Keep **every public method name** (PascalCased) and **every summary/config key string** exactly. Keep early returns, `changed` flags, epsilon comparisons, clamping order, and iteration order.
- `get_summary()` builds a new `GdDict` in the same key insertion order as the GDScript. `apply_summary(d)` keeps the same validation (`hazard_kind` guards, type checks) and the same fallbacks.
- `static func load_*()` catalog loaders stay `static` and use `CatalogRegistry`.
- **Constants:** `const X := 5` becomes `public const long X = 5;`. Dictionaries and arrays become `static readonly GdDict` or `GdArray`, and are never mutated. `enum` becomes a C# enum with identical integer values.

## Scene-touching code

Some `RefCounted` files also create or walk scene nodes (`Node3D`, `MeshInstance3D`, `Area3D`, materials, `add_child`, `get_children`, `set_meta`). **Do not reference UnityEngine from Core.** Port the pure logic into Core. Replace the node work with one of these:
- a narrow interface the Runtime layer will implement (for example `IShipSceneRoot`), or
- a pure "plan" or "descriptor" the Runtime layer applies, for example a list of visibility changes.

Leave a clearly marked `// RUNTIME:` comment describing what the node code did, and list it in your report.

## Tests (new minimal suite)

For each ported model, write an EditMode NUnit test class. Keep it small; do not translate the Godot smokes.
1. **Round-trip:** configure as the model's Godot smoke does (`scripts/validation/<name>_smoke.gd`), call `GetSummary()`, apply it to a fresh instance, and assert both summaries are equal with `V.VariantEquals`.
2. **One to three behavior checks** taken from the smoke's assertions, the ones that matter most, such as "oxygen drains while breached" or "sealing stops the drain".

Tests must not touch UnityEngine. Use `CatalogRegistry` with `CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot)` in `[SetUp]` when a model loads data, and call `CatalogRegistry.Clear()` in `[TearDown]`.

Godot parity fixtures (tick traces, loot rolls, layouts, saves) are being captured separately into `fixtures/godot/`. Fixture-driven parity tests are added by the lead afterwards. Just keep method signatures faithful so those tests can drive them.

## Verify

```
cd tools/dotnet/SynapticSea.Core.Tests
dotnet test
```
Put the .NET SDK at `F:\Tools\dotnet` first on PATH if `dotnet` isn't found. This compiles all Core sources plus EditMode tests in seconds. **A file is done only when it compiles, its tests pass, and its behavior has been compared line by line against the GDScript.**
