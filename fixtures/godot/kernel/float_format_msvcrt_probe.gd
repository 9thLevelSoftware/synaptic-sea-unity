extends SceneTree
func _initialize():
	var rng := RandomNumberGenerator.new()
	rng.seed = 12345
	var out := []
	var specials := [0.5, 1.5, 2.5, -0.5, 0.125, 0.375, 1.0/3.0, 0.6709878396987915, 1e23, 123456789.125, 0.1, 5e-324, 1.7976931348623157e308, 0.045, 1.005, 2.675]
	for v in specials:
		for d in [0, 1, 2, 3, 5, 10, 14, 15, 16, 17, 20, 25, 30]:
			out.append({"b": _bits(v), "d": d, "s": String.num(v, d)})
	for i in range(4000):
		var b := PackedByteArray()
		b.resize(8)
		var hi := rng.randi_range(0x3E000000, 0x42F00000)
		var lo := rng.randi()
		b.encode_u32(0, lo)
		b.encode_u32(4, hi)
		var v: float = b.decode_double(0)
		var d: int = rng.randi_range(0, 20)
		out.append({"b": _bits(v), "d": d, "s": String.num(v, d), "j": JSON.stringify(v)})
	var f := FileAccess.open("res://out.json", FileAccess.WRITE)
	f.store_string(JSON.stringify(out, "", false))
	f.close()
	quit()
func _bits(v: float) -> String:
	var b := PackedByteArray()
	b.resize(8)
	b.encode_double(0, v)
	return "%08x%08x" % [b.decode_u32(4), b.decode_u32(0)]
