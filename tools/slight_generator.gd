extends SceneTree
## Dumps TimelineGenerator.generate() for Cuedeck's golden comparison.

const R := TimelineTrack.Row
const T := TimelineTrack.Target

func _initialize() -> void:
	_run.call_deferred()

func _sections() -> Array:
	return [
		{"start": 0.0, "end": 16.0, "label": "Intro", "energy": 0.2},
		{"start": 16.0, "end": 40.0, "label": "Verse", "energy": 0.4},
		{"start": 40.0, "end": 64.0, "label": "Chorus", "energy": 0.9},
		{"start": 64.0, "end": 80.0, "label": "Bridge", "energy": 0.3},
		{"start": 80.0, "end": 104.0, "label": "Chorus", "energy": 0.95},
		{"start": 104.0, "end": 120.0, "label": "Outro", "energy": 0.2},
	]

func _beats(n := 240) -> PackedFloat32Array:
	var b := PackedFloat32Array()
	for i in range(n):
		b.append(i * 0.5)
	return b

func _tracks() -> Array:
	return [
		{"key": "master", "target": T.MASTER, "rows": TimelineTrack.ROW_ORDER.duplicate()},
		{"key": "par1", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "par2", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "bar", "target": T.FIXTURE, "rows": [R.STROBE, R.COLOUR, R.DIMMER]},
		{"key": "bar/h0", "target": T.HEAD, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "bar/h1", "target": T.HEAD, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "bar/h2", "target": T.HEAD, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "spot", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER]},
		{"key": "spot2", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER]},
	]

func _env() -> Dictionary:
	var peak := PackedFloat32Array()
	var lo := PackedFloat32Array()
	var hi := PackedFloat32Array()
	for c in range(1200):
		var t := c * 0.1
		var e := 0.5
		for sec in _sections():
			if t >= sec["start"] and t < sec["end"]:
				e = sec["energy"]
		if t >= 48.0 and t < 52.0:
			e = 0.1
		if t >= 18.0 and t < 20.0:
			e *= 0.6
		peak.append(e)
		lo.append(0.8 if fmod(t, 1.0) < 0.5 else 0.2)
		hi.append(0.9 if t >= 84.0 and t < 92.0 else 0.3)
	return {"peak": peak, "lo": lo, "hi": hi}

func _spread_positions() -> Array:
	return [{"id": "wide", "spread": 0.4}, {"id": "narrow", "spread": 0.0}, {"id": "mid", "spread": 0.2}, {"id": "wider", "spread": 0.3}]

func _cat_tracks() -> Array:
	var out: Array = [
		{"key": "master", "target": T.MASTER, "rows": TimelineTrack.ROW_ORDER.duplicate()},
		{"key": "w1", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER], "category": "Primary Wash", "group": 1},
		{"key": "w2", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER], "category": "Primary Wash", "group": 2},
		{"key": "w3", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER], "category": "Primary Wash", "group": 2},
		{"key": "ws", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER], "category": "Secondary Wash", "group": 1},
		{"key": "m1", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER], "category": "Primary Mover", "group": 1},
		{"key": "m2", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER], "category": "Primary Mover", "group": 1},
		{"key": "ms", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER], "category": "Secondary Mover", "group": 1},
	]
	for b in ["b1", "b2"]:
		out.append({"key": b, "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER], "category": "Primary Multi-cell", "group": 1})
		for h in range(4):
			out.append({"key": "%s/h%d" % [b, h], "target": T.HEAD, "rows": [R.COLOUR, R.DIMMER]})
	return out

func _head_mover_tracks() -> Array:
	var out: Array = [
		{"key": "master", "target": T.MASTER, "rows": TimelineTrack.ROW_ORDER.duplicate()},
		{"key": "w1", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "w2", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "spot", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.DIMMER]},
	]
	for m in ["mh1", "mh2"]:
		out.append({"key": m, "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.COLOUR, R.DIMMER]})
		for h in range(3):
			out.append({"key": "%s/h%d" % [m, h], "target": T.HEAD, "rows": [R.COLOUR, R.DIMMER]})
	return out

func _wheel_tracks() -> Array:
	var wheel := [Color.WHITE, Color(1, 0, 0), Color(0, 1, 0), Color(0, 0, 1), Color(1, 1, 0)]
	return [
		{"key": "master", "target": T.MASTER, "rows": TimelineTrack.ROW_ORDER.duplicate()},
		{"key": "par", "target": T.FIXTURE, "rows": [R.COLOUR, R.DIMMER]},
		{"key": "spot", "target": T.FIXTURE, "rows": [R.POSITION, R.MOVEMENT, R.COLOUR, R.DIMMER], "wheel": wheel},
	]

func _col(c: Color) -> Array:
	return [c.r, c.g, c.b, c.a]

func _dump(show: Dictionary) -> Dictionary:
	var out := {}
	for k in show:
		var c: Dictionary = show[k]
		var d := []
		for p in c["dimmer"]:
			d.append([p.x, p.y])
		var col := []
		for s in c["colour"]:
			col.append([s["t0"], s["t1"], _col(s["c0"]), _col(s["c1"])])
		var st := []
		for s in c["strobe"]:
			st.append([s["t0"], s["t1"], s["speed"]])
		var pos := []
		for s in c["position"]:
			pos.append([s["t0"], s["t1"], s["pos"]])
		var mv := []
		for s in c["movement"]:
			mv.append([s["t0"], s["t1"], s["fx"], s["period"], s["size"], s.get("phase", 0.0)])
		out[k] = {"dimmer": d, "colour": col, "strobe": st, "position": pos, "movement": mv}
	return out

func _run() -> void:
	var cases := []
	var add := func(name: String, tracks: Array, positions: Array, seed: String, v: int, env: Dictionary, bar_phase := 0):
		var show := TimelineGenerator.generate(_sections(), _beats(), 4, 120.0, tracks, positions, seed, v, bar_phase, env)
		cases.append({"name": name, "show": _dump(show)})
	for v in range(3):
		add.call("plain", _tracks(), ["posA", "posB"], "song.mp3", v, {})
	for v in [1, 3]:
		add.call("env", _tracks(), _spread_positions(), "valid.mp3", v, _env())
	add.call("phase", _tracks(), ["posA", "posB", "posC"], "phase.mp3", 2, _env(), 1)
	for v in range(3):
		add.call("cats", _cat_tracks(), ["posA", "posB"], "cats.mp3", v, {})
	add.call("cats_env", _cat_tracks(), _spread_positions(), "cats.mp3", 5, _env())
	for v in range(3):
		add.call("heads", _head_mover_tracks(), ["posA", "posB"], "heads.mp3", v, {})
	add.call("heads_env", _head_mover_tracks(), ["posA", "posB"], "heads.mp3", 7, _env())
	for v in range(2):
		add.call("wheel", _wheel_tracks(), ["posA"], "wheel.mp3", v, {})
	var f := FileAccess.open(OS.get_cmdline_user_args()[0], FileAccess.WRITE)
	f.store_string(JSON.stringify(cases, "", false, true))
	f.close()
	print("wrote %d cases" % cases.size())
	quit(0)
