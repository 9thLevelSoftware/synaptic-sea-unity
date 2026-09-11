extends SceneTree

## Exports Godot 4.7.1 reference data for the L4-L6 procgen ports and the systems that waited on
## layout_serializer: LifeBoatBuilder.build_layout, StartSceneBuilder data path, ShipGenerator data path,
## ComponentPlacementState, PillarPersistence and a WorkActionDriver trace. Replayed by
## SynapticSea/Assets/_Project/Tests/EditMode/Parity/ProcgenExtrasParityTests.cs. See README.md.
##
##   --out <file>       output JSON path
##   --fixtures <dir>   <repo>/fixtures/godot/procgen (layout_*.json inputs)

const ShipBlueprintScript := preload("res://scripts/procgen/ship_blueprint.gd")
const ShipLayoutGeneratorScript := preload("res://scripts/procgen/ship_layout_generator.gd")
const GameplaySliceBuilderScript := preload("res://scripts/procgen/gameplay_slice_builder.gd")
const LifeBoatBuilderScript := preload("res://scripts/procgen/life_boat.gd")
const StartSceneBuilderScript := preload("res://scripts/procgen/start_scene_builder.gd")
const ShipGeneratorScript := preload("res://scripts/procgen/ship_generator.gd")
const SeedDeterminismContractScript := preload("res://scripts/procgen/seed_determinism_contract.gd")
const ComponentCatalogScript := preload("res://scripts/systems/component_catalog.gd")
const ComponentPlacementStateScript := preload("res://scripts/systems/component_placement_state.gd")
const PillarPersistenceScript := preload("res://scripts/systems/pillar_persistence.gd")
const ModuleIntegrityMapScript := preload("res://scripts/systems/module_integrity_map.gd")
const WorkActionStateScript := preload("res://scripts/systems/work_action_state.gd")
const WorkActionCatalogScript := preload("res://scripts/systems/work_action_catalog.gd")
const WorkActionDriverScript := preload("res://scripts/systems/work_action_driver.gd")
const DetectionStateScript := preload("res://scripts/systems/detection_state.gd")

const LAYOUT_TAGS: Array = [
	"s17_medium_pristine", "s42_breach_field_standard", "s42_dead_fleet_deep_dive",
	"s42_small_wrecked_ext", "s7777_small_wrecked_ext", "s777_small_wrecked_ext", "s999_small_wrecked_ext",
]
const GOLDENS: Array = ["coherent_ship_001", "coherent_ship_002", "coherent_ship_003"]

var fixtures_dir: String = ""


func _init() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	var out_path: String = ""
	var i: int = 0
	while i < args.size():
		if args[i] == "--out":
			out_path = args[i + 1]
			i += 1
		elif args[i] == "--fixtures":
			fixtures_dir = args[i + 1]
			i += 1
		i += 1
	if out_path.is_empty() or fixtures_dir.is_empty():
		push_error("usage: --out <file> --fixtures <repo>/fixtures/godot/procgen")
		quit(2)
		return
	var result: Dictionary = {
		"engine_version": Engine.get_version_info()["string"],
		"life_boat": _life_boat(),
		"start_scene": _start_scene(),
		"ship_generator": _ship_generator(),
		"component_placement": _component_placement(),
		"pillar_persistence": _pillar_persistence(),
		"work_action_driver": _work_action_driver(),
	}
	var f := FileAccess.open(out_path, FileAccess.WRITE)
	f.store_string(JSON.stringify(_canonical(result), "", true, true) + "\n")
	f.close()
	print("wrote extras")
	quit(0)


func _life_boat() -> Dictionary:
	var out: Dictionary = {}
	for biome in ["", "breach_field", "dead_fleet"]:
		out[biome if not biome.is_empty() else "default"] = LifeBoatBuilderScript.build_layout(biome)
	out["graph"] = LifeBoatBuilderScript.build_graph().to_dict()
	return out


## StartSceneBuilder.build() data path without the loaders (no wrapper scenes in the export project).
func _start_scene() -> Array:
	var out: Array = []
	var archetype: Dictionary = StartSceneBuilderScript._load_archetype(StartSceneBuilderScript.DERELICT_ARCHETYPE_PATH)
	for seed_value in [7, 42]:
		var blueprint = StartSceneBuilderScript._build_blueprint(archetype, seed_value)
		var derelict_layout: Dictionary = ShipLayoutGeneratorScript.new().generate(blueprint, archetype)
		var lb_layout: Dictionary = LifeBoatBuilderScript.build_layout()
		var slice_builder = GameplaySliceBuilderScript.new()
		var derelict_gameplay: Dictionary = slice_builder.build(derelict_layout)
		var lb_gameplay: Dictionary = slice_builder.build(lb_layout)
		var dock_pos: Vector3 = StartSceneBuilderScript._find_dock_position(derelict_layout)
		# Full-precision module_damage plus a hash of the text without it: the 14-digit JSON floats of the damage
		# amounts are where kernel float formatting can differ (see ProcgenPipelineParityTests).
		var no_damage: Dictionary = derelict_layout.duplicate(true)
		no_damage.erase("module_damage")
		out.append({
			"seed": seed_value,
			"derelict_layout_fnv1a": SeedDeterminismContractScript.fnv1a_64(JSON.stringify(derelict_layout, "  ")),
			"derelict_layout_no_damage_fnv1a": SeedDeterminismContractScript.fnv1a_64(JSON.stringify(no_damage, "  ")),
			"derelict_module_damage": derelict_layout.get("module_damage", []),
			"derelict_gameplay": derelict_gameplay,
			"life_boat_gameplay": lb_gameplay,
			"life_boat_layout_fnv1a": SeedDeterminismContractScript.fnv1a_64(JSON.stringify(lb_layout, "  ")),
			"life_boat_position": dock_pos + Vector3(0.0, 0.0, StartSceneBuilderScript.DOCK_GAP),
		})
	return out


## ShipGenerator.generate() / generate_from_seed() data path up to the temp-file writes.
func _ship_generator() -> Array:
	var out: Array = []
	var cases: Array = [
		[1, 1, 42, "breach_field", "standard"],
		[1, 2, 7, "dead_fleet", "deep_dive"],
		[2, 0, 17, "", ""],
	]
	for c in cases:
		var gen = ShipGeneratorScript.new()
		gen.configure_run_context(c[3], c[4])
		var blueprint = ShipBlueprintScript.new(c[0], c[1], c[2])
		var archetype: Dictionary = {}
		if not str(c[3]).is_empty() or not str(c[4]).is_empty():
			archetype = gen._default_derelict_archetype()
		var layout: Dictionary = gen.layout_generator.generate_with_options(blueprint, archetype, c[3], c[4], gen._extended_for(c[4]))
		var gameplay: Dictionary = GameplaySliceBuilderScript.new().build(layout)
		var layout_arcs: Variant = layout.get("arc_zones", [])
		var slice_arcs: Variant = gameplay.get("arc_zones", [])
		if (not (layout_arcs is Array) or (layout_arcs as Array).is_empty()) and slice_arcs is Array and not (slice_arcs as Array).is_empty():
			layout["arc_zones"] = (slice_arcs as Array).duplicate(true)
		var layout_json: String = JSON.stringify(layout, "  ")
		var gameplay_json: String = JSON.stringify(gameplay, "  ")
		var no_damage: Dictionary = layout.duplicate(true)
		no_damage.erase("module_damage")
		out.append({
			"layout_no_damage_fnv1a": SeedDeterminismContractScript.fnv1a_64(JSON.stringify(no_damage, "  ")),
			"module_damage": layout.get("module_damage", []),
			"size": c[0], "condition": c[1], "seed": c[2], "biome": c[3], "difficulty": c[4],
			"layout_json_fnv1a": SeedDeterminismContractScript.fnv1a_64(layout_json),
			"layout_json_length": layout_json.length(),
			"gameplay_json_fnv1a": SeedDeterminismContractScript.fnv1a_64(gameplay_json),
			"gameplay_slice": gameplay,
			"kit_path": gen.kit_path_for_layout(layout),
		})
	return out


func _component_placement() -> Array:
	var out: Array = []
	var cat = ComponentCatalogScript.new()
	cat.load_default()
	var systems_doc: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://data/ship_systems/systems.json"))
	var layouts: Array = []
	for tag in LAYOUT_TAGS:
		var path: String = fixtures_dir.path_join("layout_%s.fullprec.json" % tag)
		if not FileAccess.file_exists(path):
			path = fixtures_dir.path_join("layout_%s.json" % tag)
		layouts.append([tag, JSON.parse_string(FileAccess.get_file_as_string(path))])
	for golden in GOLDENS:
		layouts.append([golden, JSON.parse_string(FileAccess.get_file_as_string("res://data/procgen/golden/%s/layout.json" % golden))])
	for pair in layouts:
		for seed_value in [99, 42]:
			var place = ComponentPlacementStateScript.new()
			var n: int = place.populate(pair[1], cat, seed_value, {"%s|%d|%d" % ["dock_01", 0, 0]: true})
			var entry: Dictionary = {"layout": pair[0], "seed": seed_value, "count": n,
				"occupancy_keys": Array(place.occupancy_keys()), "collisions": place.has_slot_collisions()}
			entry["linked"] = place.link_ship_systems(systems_doc, cat)
			entry["summary"] = place.get_summary()
			entry["fingerprint"] = place.fingerprint()
			var ops: Array = []
			if not place.placed.is_empty():
				var first: Dictionary = place.placed[0]
				var iid: String = str(first["component_instance_id"])
				var inv: Dictionary = {}
				ops.append(place.dismount(iid))
				ops.append(place.dismount(iid))
				ops.append(place.mount(str(first["item_form"]), str(first["room_id"]), str(first["slot_kind"]), int(first["slot_index"]), inv, cat))
				inv[str(first["item_form"])] = 2
				ops.append(place.mount("wrong_form", str(first["room_id"]), str(first["slot_kind"]), int(first["slot_index"]), {"wrong_form": 1}, cat))
				ops.append(place.mount(str(first["item_form"]), str(first["room_id"]), str(first["slot_kind"]), int(first["slot_index"]), inv, cat))
				ops.append(place.mount(str(first["item_form"]), "fresh_room", "center", 7, inv, cat))
				ops.append(place.mount(str(first["item_form"]), "fresh_room", "center", 8, inv, null))
				ops.append(inv)
				ops.append([place.mounted_count(), place.dismounted_count(), place.is_mounted(iid), place.find_index("nope")])
			entry["ops"] = ops
			entry["summary_after_ops"] = place.get_summary()
			out.append(entry)
	return out


func _pillar_persistence() -> Dictionary:
	var map = ModuleIntegrityMapScript.new()
	map.ensure_module("eng/wall_a", "wall_straight_1x1", {}, "eng")
	map.apply_damage("eng/wall_a", 0.45, "wall_straight_1x1")
	map.apply_damage("cargo/floor_2", 0.9, "floor_1x1")
	var cat = ComponentCatalogScript.new()
	cat.load_default()
	var place = ComponentPlacementStateScript.new()
	place.populate(JSON.parse_string(FileAccess.get_file_as_string("res://data/procgen/golden/coherent_ship_001/layout.json")), cat, 5)
	var wa_cat = WorkActionCatalogScript.new()
	wa_cat.load_default()
	var work = WorkActionStateScript.new()
	work.configure_action("pry_panel", wa_cat.get_action("pry_panel"))
	work.start("panel_1", {"tool_class": "prybar", "skill_id": "salvage", "skill_level": 0, "inventory": {}})
	work.tick(1.0, {})
	var bundle: Dictionary = PillarPersistenceScript.pack_all(map, place, work)
	var unpacked: Dictionary = PillarPersistenceScript.unpack_all(bundle)
	var repacked: Dictionary = PillarPersistenceScript.pack_all(unpacked["module_integrity"], unpacked["component_placement"], unpacked["work_action"])
	var idle_work = WorkActionStateScript.new()
	return {
		"bundle": bundle,
		"repacked": repacked,
		"null_bundle": PillarPersistenceScript.pack_all(null, null, null),
		"idle_work": PillarPersistenceScript.pack_work_action(idle_work),
		"unpacked_idle": PillarPersistenceScript.unpack_work_action({"active": false}).get_summary(),
		"sanitized": PillarPersistenceScript.sanitize_historical({"module_integrity_summary": 5, "other": [1]}),
	}


func _work_action_driver() -> Array:
	var trace: Array = []
	var driver = WorkActionDriverScript.new()
	driver.configure({"cart_capacity": 100.0, "cart_mass": 0.0})
	var map = ModuleIntegrityMapScript.new()
	map.ensure_module("eng/wall_a", "wall_straight_1x1", {}, "eng")
	var inv: Dictionary = {}
	var ctx: Dictionary = {"tool_class": "welding_lance", "skill_id": "salvage", "skill_level": 0, "inventory": inv}
	trace.append(["start", driver.start_action("cut_wall", "eng/wall_a", ctx), driver.get_status()])
	trace.append(["tick", driver.tick(0.5, {}), driver.progress_ratio(), driver.last_progress_noise, driver.last_noise_pulse])
	trace.append(["tick", driver.tick(0.7, {}), driver.progress_ratio(), driver.last_progress_noise, driver.last_noise_pulse])
	trace.append(["tick", driver.tick(0.1, {"damaged": true}), driver.get_status()])
	trace.append(["complete", driver.complete(map, inv)])
	driver.reset()
	trace.append(["start", driver.start_action("cut_wall", "eng/wall_a", {"tool_class": "welding_lance", "skill_id": "salvage", "skill_level": 0, "inventory": {}}), driver.get_status()])
	trace.append(["tick", driver.tick(20.0, {"work_speed_mult": 1.0}), driver.progress_ratio()])
	trace.append(["complete", driver.complete(map, inv), inv.duplicate(true), map.get_state("eng/wall_a"), driver.last_noise_pulse, driver.last_xp_event, driver.cart_mass, driver.overloaded])
	var det = DetectionStateScript.new()
	det.configure({"noise_level": 0.1})
	var dict_target: Dictionary = {"player_noise": 0.2}
	trace.append(["noise", driver.apply_noise_to_detection(det), det.noise_level, driver.apply_noise_to_detection(dict_target), dict_target])
	trace.append(["targets", driver.list_targets("cut", map, null)])
	driver.cart_mass = 200.0
	driver.overloaded = true
	trace.append(["start_overloaded", driver.start_action("cut_wall", "eng/wall_b", {"tool_class": "welding_lance", "skill_id": "salvage", "skill_level": 0, "inventory": {}})])
	map.ensure_module("eng/wall_b", "wall_straight_1x1", {}, "eng")
	map.apply_damage("eng/wall_b", 0.4, "wall_straight_1x1")
	trace.append(["start_weld", driver.start_action("weld_patch", "eng/wall_b", {"tool_class": "welding_lance", "skill_id": "repair", "skill_level": 0, "inventory": {"hull_plate": 1}})])
	trace.append(["tick", driver.tick(20.0, {})])
	var inv2: Dictionary = {"hull_plate": 1}
	trace.append(["complete_weld", driver.complete(map, inv2), inv2, map.get_state("eng/wall_b"), driver.cart_mass])
	driver.reset()
	driver.configure({})
	trace.append(["start_pry", driver.start_action("pry_panel", "panel_1", {"tool_class": "prybar", "skill_id": "salvage", "skill_level": 0, "inventory": {}})])
	trace.append(["tick", driver.tick(1.0, {}), driver.progress_ratio(), driver.last_progress_noise])
	trace.append(["tick", driver.tick(0.4, {}), driver.last_progress_noise, driver.last_noise_pulse])
	var snap: Dictionary = driver.get_persistence_summary()
	var d2 = WorkActionDriverScript.new()
	d2.configure({})
	trace.append(["persist", snap, d2.apply_persistence_summary(snap), d2.get_status(), d2.progress_ratio()])
	trace.append(["context", driver.build_context("prybar", "salvage", 2, {"scrap_metal": 3}, null, "", true)])
	trace.append(["complete_not_done", driver.complete(null, {})])
	driver.reset()
	trace.append(["complete_no_work", driver.complete(null, {}), driver.get_status(), driver.is_working()])
	trace.append(["unknown_action", driver.start_action("no_such_action", "x", {})])
	return trace


func _canonical(value: Variant) -> Variant:
	if value is Vector2i:
		return [int(value.x), int(value.y)]
	if value is Vector3:
		return [float(value.x), float(value.y), float(value.z)]
	if value is StringName:
		return str(value)
	if value is PackedStringArray:
		return _canonical(Array(value))
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
