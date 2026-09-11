extends SceneTree

## Exports Godot reference outputs for the Wave 2 (E2) procgen pipeline stages:
## TemplateSelector, RoomAssigner, RoomGraphGenerator, StructuralPlacer, EncounterInjector,
## StructuralEdgeCompiler, FirstRunContract. Replayed by
## SynapticSea/Assets/_Project/Tests/EditMode/Procgen/ProcgenStageParityTests.cs.
## See README.md in this folder for the project setup and command line.
##
##   --out <dir>        output directory (one <stage>.json per stage)
##   --fixtures <dir>   <repo>/fixtures/godot/procgen (layout / recipe / gameplay_slice inputs)
##   --stage <name>     export one stage only
##   --full             broad validation sweep (more seeds/sizes/layouts, tab-indented); the committed
##                      fixtures are the default (reduced, compact) set

const TemplateSelectorScript := preload("res://scripts/procgen/template_selector.gd")
const RoomAssignerScript := preload("res://scripts/procgen/room_assigner.gd")
const RoomGraphGeneratorScript := preload("res://scripts/procgen/room_graph_generator.gd")
const StructuralPlacerScript := preload("res://scripts/procgen/structural_placer.gd")
const EncounterInjectorScript := preload("res://scripts/procgen/encounter_injector.gd")
const StructuralEdgeCompilerScript := preload("res://scripts/procgen/structural_edge_compiler.gd")
const FirstRunContractScript := preload("res://scripts/procgen/first_run_contract.gd")
const ShipBlueprintScript := preload("res://scripts/procgen/ship_blueprint.gd")
const RoomVariantSelectorScript := preload("res://scripts/procgen/room_variant_selector.gd")
const BiomeProfileScript := preload("res://scripts/procgen/biome_profile.gd")
const DifficultyProfileScript := preload("res://scripts/procgen/difficulty_profile.gd")

const SEEDS: Array = [0, 1, 7, 17, 42, 777, 999, 7777, 123456789, -5]
const ARCHETYPES: Array = ["", "derelict", "small_freighter", "medium_cruiser", "life_boat"]
const BIOMES: Array = ["", "abyssal_synaptic_sea", "breach_field", "dead_fleet"]
const DIFFICULTIES: Array = ["", "standard", "hardened", "deep_dive"]
const LAYOUT_TAGS: Array = [
	"s17_medium_pristine",
	"s42_abyssal_synaptic_sea_deep_dive", "s42_abyssal_synaptic_sea_hardened", "s42_abyssal_synaptic_sea_standard",
	"s42_breach_field_deep_dive", "s42_breach_field_hardened", "s42_breach_field_standard",
	"s42_dead_fleet_deep_dive", "s42_dead_fleet_hardened", "s42_dead_fleet_standard",
	"s42_small_wrecked_ext", "s7777_small_wrecked_ext", "s777_small_wrecked_ext", "s999_small_wrecked_ext",
]
const ENCOUNTER_GRID_TAGS: Array = [
	"s17_medium_pristine", "s42_breach_field_standard", "s777_small_wrecked_ext", "s999_small_wrecked_ext",
]
const COMPILER_VARIANT_TAGS: Array = ["s999_small_wrecked_ext"]

var out_dir: String = ""
var fixtures_dir: String = ""
var only_stage: String = ""
var full: bool = false


func _init() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	var i: int = 0
	while i < args.size():
		match args[i]:
			"--out":
				out_dir = args[i + 1]
				i += 1
			"--fixtures":
				fixtures_dir = args[i + 1]
				i += 1
			"--stage":
				only_stage = args[i + 1]
				i += 1
			"--full":
				full = true
		i += 1
	if out_dir.is_empty() or fixtures_dir.is_empty():
		push_error("usage: --out <dir> --fixtures <repo>/fixtures/godot/procgen [--stage name] [--full]")
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(out_dir)
	var stages: Array = ["template_selector", "room_assigner", "room_graph_generator", "structural_placer",
		"encounter_injector", "structural_edge_compiler", "first_run_contract"]
	for stage in stages:
		if not only_stage.is_empty() and only_stage != stage:
			continue
		var t0: int = Time.get_ticks_msec()
		var result: Dictionary = call("_stage_" + stage)
		result["engine_version"] = Engine.get_version_info()["string"]
		_write(stage, result)
		print("stage %s done in %d ms" % [stage, Time.get_ticks_msec() - t0])
	quit(0)


func _write(stage: String, value: Variant) -> void:
	var f := FileAccess.open(out_dir.path_join(stage + ".json"), FileAccess.WRITE)
	# Full precision so the C# replay compares floats bit-exactly; ints stay ints (17 vs 17.0).
	f.store_string(JSON.stringify(_canonical(value), "\t" if full else "", true, true) + "\n")
	f.close()


## Same canonical form as scripts/validation/procgen_structural_debug_export.gd::_canonical_value
## (Vector2i -> [x, y], Vector3 -> [x, y, z]); JSON.stringify sorts keys.
func _canonical(value: Variant) -> Variant:
	if value is Vector2i:
		return [int(value.x), int(value.y)]
	if value is Vector3:
		return [float(value.x), float(value.y), float(value.z)]
	if value is Dictionary:
		var out: Dictionary = {}
		for k in value.keys():
			out[str(k)] = _canonical(value[k])
		return out
	if value is Array:
		var arr: Array = []
		for item in value:
			arr.append(_canonical(item))
		return arr
	return value


func _load_json(path: String) -> Variant:
	return JSON.parse_string(FileAccess.get_file_as_string(path))


func _archetype(name: String) -> Dictionary:
	if name.is_empty():
		return {}
	return _load_json("res://data/procgen/archetypes/%s.json" % name)


func _layout_path(tag: String) -> String:
	var fp: String = fixtures_dir.path_join("layout_%s.fullprec.json" % tag)
	if FileAccess.file_exists(fp):
		return fp
	return fixtures_dir.path_join("layout_%s.json" % tag)


func _vec_cells(layout: Dictionary) -> void:
	for room in layout["rooms"]:
		var cells: Array = []
		for c in room["cells"]:
			cells.append(Vector2i(int(c[0]), int(c[1])))
		room["cells"] = cells


# ------------------------------------------------------------------ stages

func _stage_template_selector() -> Dictionary:
	var sel = TemplateSelectorScript.new()
	var cases: Array = []
	for s in SEEDS:
		var bp = ShipBlueprintScript.new(1, 1, s)
		var entry: Dictionary = {"seed": s}
		entry["select"] = str(sel.select(bp, {}).id)
		for flags in [[false, false], [true, false], [false, true], [true, true]]:
			var t = sel.select_with_options(bp, {}, flags[0], flags[1])
			entry["opts_%s_%s" % [str(flags[0]), str(flags[1])]] = str(t.id)
		entry["explicit"] = str(sel.select_with_options(bp, {"template": "ring"}, false, false).id)
		cases.append(entry)
	var templates: Dictionary = {}
	for tid in TemplateSelectorScript.EXTENDED_TEMPLATES:
		var t = sel.select(ShipBlueprintScript.new(), {"template": tid})
		templates[tid] = {"id": t.id, "zones": t.zones, "connections": t.connections, "deck_config": t.deck_config}
	return {
		"cases": cases,
		"available": [sel.available_templates(false, false), sel.available_templates(true, false),
			sel.available_templates(false, true), sel.available_templates(true, true)],
		"catalog_size_on_disk": sel.catalog_size_on_disk(),
		"templates": templates,
	}


func _stage_room_assigner() -> Dictionary:
	var sel = TemplateSelectorScript.new()
	var cases: Array = []
	var key_order: Array = []
	var seeds: Array = SEEDS if full else [7, 42]
	var biomes: Array = ["<none>", "", "breach_field", "dead_fleet"] if full else ["<none>", "dead_fleet"]
	var sizes: Array = [0, 1, 2] if full else [1, 2]
	for tid in TemplateSelectorScript.EXTENDED_TEMPLATES:
		var template = sel.select(ShipBlueprintScript.new(), {"template": tid})
		for arch_name in ARCHETYPES:
			var arch: Dictionary = _archetype(arch_name)
			for size in sizes:
				for s in seeds:
					for biome in biomes:
						var bp = ShipBlueprintScript.new(size, 1, s)
						var ra = RoomAssignerScript.new()
						var plan: Array
						if biome == "<none>":
							plan = ra.assign(template, bp, arch.duplicate(true))
						else:
							plan = ra.assign_with_selector(template, bp, arch.duplicate(true), RoomVariantSelectorScript.new(), biome)
						for room in plan:
							if key_order.is_empty():
								key_order = room.keys()
							elif room.keys() != key_order:
								push_error("room key order differs: %s" % str(room.keys()))
						cases.append({"template": tid, "archetype": arch_name, "size": size, "seed": s, "biome": biome,
							"plan": plan})
	var normalized: Dictionary = {}
	for arch_name in ARCHETYPES:
		normalized[arch_name] = RoomAssignerScript.normalize_archetype(_archetype(arch_name))
	return {"cases": cases, "key_order": key_order, "normalized_archetypes": normalized}


func _graph_cases() -> Array:
	var cases: Array = []
	for arch_name in ARCHETYPES:
		var arch: Dictionary = _archetype(arch_name)
		for size in [0, 1, 2]:
			for s in SEEDS:
				var bp = ShipBlueprintScript.new(size, 0, s)
				var g = RoomGraphGeneratorScript.new().generate(bp, arch.duplicate(true))
				cases.append({"archetype": arch_name, "size": size, "seed": s, "graph": g})
	return cases


func _stage_room_graph_generator() -> Dictionary:
	var out: Array = []
	for c in _graph_cases():
		out.append({"archetype": c.archetype, "size": c.size, "seed": c.seed, "graph": c.graph.to_dict()})
	return {"cases": out}


## Runs the real place_structure() and walks the returned Node3D tree. Wrapper scenes are not part of
## the export project, so module children are absent; the module list comes from _modules_for_role().
func _stage_structural_placer() -> Dictionary:
	var out: Array = []
	for c in _graph_cases():
		for biome in ["", "breach_field"]:
			var placer = StructuralPlacerScript.new()
			var root: Node3D = placer.place_structure(c.graph, c.seed, biome)
			var has_root: bool = root != null
			var rooms: Array = []
			if has_root:
				for room_node in root.get_children():
					var role: String = str(c.graph.get_room(str(room_node.name)).get("role", ""))
					rooms.append({"name": str(room_node.name), "position": room_node.position,
						"modules": placer._modules_for_role(role)})
				root.free()
			out.append({"archetype": c.archetype, "size": c.size, "seed": c.seed, "biome": biome,
				"root": has_root, "rooms": rooms})
	return {"cases": out}


func _stage_encounter_injector() -> Dictionary:
	var out: Array = []
	var combos: Array = []
	var seeds: Array = [42, 7, 999] if full else [42, 7]
	for b in BIOMES:
		for d in DIFFICULTIES:
			for s in seeds:
				combos.append([b, d, s])
	for tag in LAYOUT_TAGS:
		var text: String = FileAccess.get_file_as_string(_layout_path(tag))
		var recipe: Dictionary = _load_json(fixtures_dir.path_join("recipe_%s.json" % tag))
		# Recipe replay: same biome / difficulty / seed as the capture pipeline.
		var variants: Array = [["recipe", str(recipe.get("biome_id", "")), str(recipe.get("difficulty_id", "")),
			int(recipe.get("blueprint", {}).get("seed_value", 0))]]
		if full or tag in ENCOUNTER_GRID_TAGS:
			for c in combos:
				variants.append(["grid", c[0], c[1], c[2]])
			variants.append(["no_critical_path", "breach_field", "deep_dive", 42])
			variants.append(["no_cells", "breach_field", "deep_dive", 42])
			variants.append(["vec_cells", "dead_fleet", "hardened", 7])
		for v in variants:
			var layout: Dictionary = JSON.parse_string(text)
			layout.erase("encounters")
			layout.erase("encounter_pacing")
			if v[0] == "no_critical_path":
				layout.erase("critical_path")
			elif v[0] == "no_cells":
				for room in layout["rooms"]:
					room.erase("cells")
			elif v[0] == "vec_cells":
				_vec_cells(layout)
			var biome = null
			if not str(v[1]).is_empty():
				biome = BiomeProfileScript.from_file("res://data/procgen/biomes/%s.json" % v[1])
			var difficulty = null
			if not str(v[2]).is_empty():
				difficulty = DifficultyProfileScript.for_id(v[2])
			var result: Dictionary = EncounterInjectorScript.new().inject(layout, biome, difficulty, int(v[3]))
			out.append({"layout": tag, "variant": v[0], "biome": v[1], "difficulty": v[2], "seed": v[3],
				"encounters": result.get("encounters"), "encounter_pacing": result.get("encounter_pacing", null),
				"validate": EncounterInjectorScript.validate(result)})
	return {"cases": out}


## The plain compile of every capture layout (JSON cells or Vector2i cells) must equal the layout's own
## structural_plan, which the C# test checks directly; only the perturbed "hatch_breach" variant and the
## malformed layouts need exported expectations (all variants with --full).
func _stage_structural_edge_compiler() -> Dictionary:
	var out: Array = []
	var tags: Array = LAYOUT_TAGS if full else COMPILER_VARIANT_TAGS
	var variants: Array = ["json", "vec_cells", "hatch_breach"] if full else ["hatch_breach"]
	for tag in tags:
		var text: String = FileAccess.get_file_as_string(_layout_path(tag))
		for variant in variants:
			var layout: Dictionary = JSON.parse_string(text)
			layout.erase("structural_plan")
			if variant == "vec_cells":
				_vec_cells(layout)
			elif variant == "hatch_breach":
				var portals: Array = layout.get("portals", [])
				for pi in range(portals.size()):
					var p: Dictionary = portals[pi]
					match pi % 4:
						0: p["state"] = "hatch"
						1: p["state"] = "BREACH"
						2: p["state"] = "open"
						3: p["module_id"] = "bulkhead_portal_2x1"
				layout["kit_id"] = "ship_structural_industrial"
			var plan: Dictionary = StructuralEdgeCompilerScript.new().compile(layout)
			out.append({"layout": tag, "variant": variant, "plan": plan,
				"occupancy_keys": plan["occupancy"].keys(), "edge_keys": plan["edges"].keys()})
	# Error paths.
	var bad_layouts: Array = [
		{},
		{"rooms": []},
		{"rooms": [{"id": "a", "deck": 0, "cells": [[0, 0]]}, {"id": "a", "deck": 0, "cells": [[1, 0]]},
			{"deck": 0}, {"id": "b", "deck": "x", "cells": [[2, 0]]}, {"id": "c", "deck": 0, "cells": []},
			{"id": "d", "deck": 0.0, "cells": [[0, 0], [1.5, 0], "(3, 0)", [4, 0, 1], "junk"]},
			{"id": "e", "deck": 1, "cells": [[0, 0, 1], [1, 0]]}, 5],
			"kit_id": "", "vertical_connections": [{"from_room": "d", "to_room": "e", "from_cell": [3, 0], "to_cell": [0, 0, 1]}],
			"portals": [{"id": "p1", "from_room": "a", "to_room": "d", "from_cell": [0, 0], "to_cell": [3, 0],
				"edge_cell": [0, 0], "edge_direction": "north"},
				{"id": "p2", "from_room": "a", "to_room": "a", "from_cell": [0, 0], "to_cell": [0, 0]},
				{"id": "p3", "from_room": "d", "to_room": "e", "from_cell": [4, 0], "to_cell": [0, 0]},
				{"id": "p4", "from_room": "d", "to_room": "a", "from_cell": [3, 0], "to_cell": [0, 0]},
				"junk"],
			"blocked_links": [{"from_room": "d", "to_room": "a", "from_cell": [3, 0], "to_cell": [0, 0]}]},
	]
	for i in range(bad_layouts.size()):
		var plan: Dictionary = StructuralEdgeCompilerScript.new().compile(bad_layouts[i])
		out.append({"layout": "bad_%d" % i, "variant": "json", "plan": plan,
			"occupancy_keys": plan["occupancy"].keys(), "edge_keys": plan["edges"].keys()})
	return {"cases": out, "bad_layouts": bad_layouts}


func _stage_first_run_contract() -> Dictionary:
	var out: Array = []
	var frc = FirstRunContractScript.new()
	var loaded: bool = frc.load_contract()
	var candidates: Dictionary = {}
	for tag in LAYOUT_TAGS:
		var layout: Dictionary = _load_json(_layout_path(tag))
		var slice: Dictionary = _load_json(fixtures_dir.path_join("gameplay_slice_%s.json" % tag))
		out.append({"layout": tag, "valid": frc.validate(layout, slice), "valid_no_slice": frc.validate(layout, {}),
			"hazard": frc._has_any_required_hazard(layout, slice, ["fire_zone", "breach_zone"])})
		if tag.ends_with("small_wrecked_ext"):
			var seed_value: int = int(tag.substr(1, tag.find("_") - 1))
			candidates[seed_value] = {"layout": layout, "gameplay_slice": slice}
	var forced: Dictionary = {}
	for tag in ["s42_breach_field_standard", "s42_dead_fleet_standard"]:
		var layout: Dictionary = _load_json(_layout_path(tag))
		layout["encounters"] = [{"id": "x"}]
		layout["breach_zones"] = [{"id": "b"}]
		var slice: Dictionary = _load_json(fixtures_dir.path_join("gameplay_slice_%s.json" % tag))
		forced[tag] = frc.validate(layout, slice)
	return {"loaded": loaded, "contract": frc.contract, "cases": out, "forced": forced,
		"pick_seed_null": frc.pick_seed(null), "pick_seed_candidates": frc.pick_seed(candidates),
		"pick_seed_empty": frc.pick_seed({})}
