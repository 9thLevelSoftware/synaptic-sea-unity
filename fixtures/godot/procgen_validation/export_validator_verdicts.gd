extends SceneTree

## Exports Godot 4.7.1 StructuralPlanValidator + WalkabilityContract verdicts for the golden coherent ships and
## for layouts regenerated from the fixtures/godot/procgen recipes (plus the procgen_walkability_smoke layout).
## Replayed by SynapticSea/Assets/_Project/Tests/EditMode/Parity/ProcgenValidatorParityTests.cs. See README.md.
##
##   --out <file>       output JSON path
##   --fixtures <dir>   <repo>/fixtures/godot/procgen (recipe_*.json)

const ShipBlueprintScript := preload("res://scripts/procgen/ship_blueprint.gd")
const ShipLayoutGeneratorScript := preload("res://scripts/procgen/ship_layout_generator.gd")
const GameplaySliceBuilderScript := preload("res://scripts/procgen/gameplay_slice_builder.gd")
const StructuralEdgeCompilerScript := preload("res://scripts/procgen/structural_edge_compiler.gd")
const StructuralPlanValidatorScript := preload("res://scripts/procgen/structural_plan_validator.gd")
const WalkabilityContractScript := preload("res://scripts/procgen/walkability_contract.gd")

const GOLDENS: Array = ["coherent_ship_001", "coherent_ship_002", "coherent_ship_003"]


func _init() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	var out_path: String = ""
	var fixtures_dir: String = ""
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

	var cases: Array = []
	for golden in GOLDENS:
		var layout: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://data/procgen/golden/%s/layout.json" % golden))
		var embedded: Dictionary = layout.get("structural_plan", {})
		var compiled: Dictionary = StructuralEdgeCompilerScript.new().compile(layout)
		var entry: Dictionary = _verdicts(layout, compiled)
		entry["name"] = golden
		entry["kind"] = "golden"
		if not embedded.is_empty():
			entry["embedded_plan_verdict"] = StructuralPlanValidatorScript.new().validate(embedded, layout)
		cases.append(entry)

	var tags: Array = []
	for f in DirAccess.get_files_at(fixtures_dir):
		if f.begins_with("recipe_") and f.ends_with(".json"):
			tags.append(f.trim_prefix("recipe_").trim_suffix(".json"))
	tags.sort()
	for tag in tags:
		var recipe: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(fixtures_dir.path_join("recipe_%s.json" % tag)))
		var layout: Dictionary = _regenerate(recipe)
		var entry: Dictionary = _verdicts(layout, layout.get("structural_plan", {}))
		entry["name"] = tag
		entry["kind"] = "recipe"
		entry["recompiled_plan_verdict"] = StructuralPlanValidatorScript.new().validate(
			StructuralEdgeCompilerScript.new().compile(layout), layout)
		cases.append(entry)

	# procgen_walkability_smoke.gd layout.
	var bp = ShipBlueprintScript.new(ShipBlueprintScript.Size.MEDIUM, ShipBlueprintScript.Condition.PRISTINE, 42)
	var spine: Dictionary = ShipLayoutGeneratorScript.new().generate(bp, {"template": "spine"})
	var spine_entry: Dictionary = _verdicts(spine, StructuralEdgeCompilerScript.new().compile(spine))
	spine_entry["name"] = "spine_seed_42"
	spine_entry["kind"] = "smoke"
	cases.append(spine_entry)

	# Fail-closed paths: tampered copies of coherent_ship_001's compiled plan / topology.
	var base_layout: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://data/procgen/golden/coherent_ship_001/layout.json"))
	var base_plan: Dictionary = StructuralEdgeCompilerScript.new().compile(base_layout)
	var tampered: Array = []
	for mode in ["empty_plan", "drop_floor", "open_placement", "bad_occupancy", "no_bindings", "bad_portal", "drop_ceiling", "bad_yaw"]:
		var plan: Dictionary = base_plan.duplicate(true)
		var topo: Dictionary = base_layout.duplicate(true)
		match mode:
			"empty_plan":
				plan = {}
			"drop_floor":
				(plan["floor_placements"] as Array).remove_at(0)
			"open_placement":
				var p: Dictionary = (plan["placements"] as Array)[0]
				p["kind"] = "OPEN"
				p["module_id"] = "floor_1x1"
			"bad_occupancy":
				var first_key: String = str(plan["occupancy"].keys()[0])
				plan["occupancy"][first_key]["deck"] = "x"
				plan["occupancy"][first_key]["room_id"] = ""
			"no_bindings":
				plan["socket_bindings"] = []
				for key in ["placements", "floor_placements", "ceiling_placements"]:
					for rec in plan[key]:
						rec.erase("socket_bindings")
			"bad_portal":
				var portal: Dictionary = (topo["portals"] as Array)[0]
				portal["to_cell"] = [99, 99]
				portal["from_direction"] = "north"
				portal["to_direction"] = "east"
			"drop_ceiling":
				(plan["ceiling_placements"] as Array).remove_at(1)
				(plan["ceiling_placements"] as Array)[0]["position"] = [1.0, 2.0, 3.0]
			"bad_yaw":
				(plan["placements"] as Array)[0]["yaw_degrees"] = 45.0
				(plan["floor_placements"] as Array)[0]["yaw_degrees"] = 1.0
		tampered.append({"mode": mode, "verdict": StructuralPlanValidatorScript.new().validate(plan, topo)})

	var f := FileAccess.open(out_path, FileAccess.WRITE)
	f.store_string(JSON.stringify(_canonical({"engine_version": Engine.get_version_info()["string"], "cases": cases, "tampered": tampered}), "", true, true) + "\n")
	f.close()
	print("wrote %d cases" % cases.size())
	quit(0)


func _regenerate(recipe: Dictionary) -> Dictionary:
	var bpd: Dictionary = recipe["blueprint"]
	var bp = ShipBlueprintScript.new(int(bpd["size"]), int(bpd["condition"]), int(bpd["seed_value"]))
	bp.room_count_range = Vector2i(int(bpd["room_count_range"][0]), int(bpd["room_count_range"][1]))
	var arch: Dictionary = {}
	if recipe.has("archetype_source"):
		arch = JSON.parse_string(FileAccess.get_file_as_string(str(recipe["archetype_source"])))
	else:
		arch = _ints(recipe.get("archetype", {}))
	var layout: Dictionary = ShipLayoutGeneratorScript.new().generate_with_options(
		bp, arch, str(recipe.get("biome_id", "")), str(recipe.get("difficulty_id", "")), bool(recipe.get("extended_templates", false)))
	var gameplay: Dictionary = GameplaySliceBuilderScript.new().build(layout)
	var layout_arcs: Variant = layout.get("arc_zones", [])
	var slice_arcs: Variant = gameplay.get("arc_zones", [])
	if (not (layout_arcs is Array) or (layout_arcs as Array).is_empty()) and slice_arcs is Array and not (slice_arcs as Array).is_empty():
		layout["arc_zones"] = (slice_arcs as Array).duplicate(true)
	return layout


## Recipe archetypes captured from GDScript literals hold ints; JSON parsing turns them into floats.
func _ints(v: Variant) -> Variant:
	if v is Dictionary:
		var o: Dictionary = {}
		for k in v:
			o[k] = _ints(v[k])
		return o
	if v is Array:
		var a: Array = []
		for x in v:
			a.append(_ints(x))
		return a
	if v is float and v == floor(v):
		return int(v)
	return v


func _verdicts(layout: Dictionary, plan: Dictionary) -> Dictionary:
	var out: Dictionary = {}
	out["plan_verdict"] = StructuralPlanValidatorScript.new().validate(plan, layout)
	var occupancy: Dictionary = plan.get("occupancy", {})
	var edges: Dictionary = plan.get("edges", {})
	var enclosure: Dictionary = WalkabilityContractScript.build_adjacency(occupancy, edges, layout, false)
	var start_key: String = str(occupancy.keys()[0]) if not occupancy.is_empty() else ""
	out["enclosure_adjacency"] = enclosure
	out["enclosure_flood"] = WalkabilityContractScript.flood_visited(enclosure, start_key).keys()
	var gameplay: Dictionary = GameplaySliceBuilderScript.new().build(layout)
	var start_room: String = str(gameplay.get("start_room", ""))
	var goal_room: String = str(gameplay.get("goal_room", ""))
	var standing: Dictionary = WalkabilityContractScript.build_adjacency(occupancy, edges, layout, true)
	out["standing_adjacency"] = standing
	out["start_room"] = start_room
	out["goal_room"] = goal_room
	out["rooms_reachable"] = WalkabilityContractScript.rooms_reachable(standing, occupancy, start_room, goal_room)
	var path_keys: Array[String] = WalkabilityContractScript.standing_path_keys(standing, occupancy, start_room, goal_room)
	out["standing_path_keys"] = path_keys
	out["standing_void_reason"] = WalkabilityContractScript.standing_void_reason(plan, occupancy, path_keys, layout)
	var capsules: Dictionary = {}
	for edge_key in edges.keys():
		var edge: Dictionary = edges[edge_key]
		capsules[str(edge_key)] = [
			WalkabilityContractScript.edge_kind(edge),
			WalkabilityContractScript.capsule_hits_solid_slab(edge, occupancy),
			WalkabilityContractScript.capsule_passes_door_opening(edge, occupancy),
			WalkabilityContractScript.capsule_hits_zero_thickness_fixture(edge, occupancy),
			WalkabilityContractScript.capsule_hits_cell_center_aabb_fixture(edge, occupancy),
		]
	out["capsules"] = capsules
	return out


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
