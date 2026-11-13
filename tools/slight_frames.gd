extends SceneTree
## Records sLight's DMX output for Cuedeck's golden scenario
## (Cuedeck.Tests/Golden/GoldenScenario.cs), so Cuedeck's can be checked
## against it frame for frame. Run it with sLight's project:
##
##   Godot_v4.7-stable_mono_win64_console.exe --headless --path <sLight> \
##     --script <this repo>/tools/slight_frames.gd -- \
##     <this repo>/Cuedeck.Tests/Golden/scenario.slight \
##     <this repo>/Cuedeck.Tests/Golden/scenario.frames.json
##
## (A fresh sLight checkout needs `--headless --path <sLight> --import`
## first, so its class names are known.) The app's own settings, last show
## and autosave are left alone: the shell is pointed at a scratch folder.
##
## Keep FRAME_SECONDS, FRAMES, CHANNELS, GRAND_MASTER and SCRIPT the same as
## GoldenScenario's, and every frame in the same order as LightingShow.Tick():
## the crossfade, the cue timers, the chases and effects, then the frame.

const FRAME_SECONDS := 0.025
const FRAMES := 320
const CHANNELS := 64
const GRAND_MASTER := 0.8
const SCRIPT := [
	[0, "go"],
	[40, "effect Wave"],
	[50, "effect Pick"],
	[55, "effect Sweep"],
	[60, "chase Bounce"],
	[70, "chase Pairs"],
	[100, "go"],
	[150, "fader 1 0.3"],
	[180, "fader 0 0.9"],
	[220, "flash white"],
	[240, "flash release white"],
	[260, "back"],
	[290, "fader 1 0.0"],
	[300, "blackout"],
]

var main: Control


func _initialize() -> void:
	_run.call_deferred()


func _run() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 2:
		printerr("usage: -- <scenario.slight> <frames.json>")
		quit(2)
		return
	var scratch := OS.get_temp_dir().path_join("cuedeck_slight_frames")
	DirAccess.make_dir_recursive_absolute(scratch)
	main = load("res://main.tscn").instantiate()
	main.last_show_store = scratch.path_join("last.json")
	main.ui_state_store = scratch.path_join("ui.json")
	main.autosave.file = scratch.path_join("autosave.slight")
	main.autosave.session_file = scratch.path_join("session.json")
	root.add_child(main)
	await process_frame
	main._refresh_timer.stop()

	var parsed = JSON.parse_string(FileAccess.get_file_as_string(args[0]))
	if not parsed is Dictionary:
		printerr("not a show: ", args[0])
		quit(1)
		return
	main.apply_show_dict(parsed)
	var art := root.get_node("ArtNet")
	var fx := root.get_node("Fx")
	art.master = GRAND_MASTER

	# From here on, nothing waits for a frame: the engine's own processing
	# never runs, so only the steps below move the show on.
	var frames: Array = []
	for frame in range(FRAMES):
		for s in SCRIPT:
			if s[0] == frame:
				_do(String(s[1]), fx)
		art._process(FRAME_SECONDS)
		main.cue_panel.advance(FRAME_SECONDS)
		fx._process(FRAME_SECONDS)
		art.tick(false)
		var row: Array = []
		for i in range(art.universe_count()):
			row.append(art.get_universe(i).output.slice(0, CHANNELS).hex_encode())
		frames.append(row)

	var f := FileAccess.open(args[1], FileAccess.WRITE)
	f.store_string(JSON.stringify({"recorded_by": "sLight " + str(ProjectSettings.get_setting("application/config/version")),
		"frames": frames}))
	f.close()
	print("wrote %d frames to %s" % [frames.size(), args[1]])
	main.queue_free()
	quit(0)


func _do(action: String, fx: Node) -> void:
	var w := action.split(" ")
	match w[0]:
		"go":
			main.cue_panel.go()
		"back":
			main.cue_panel.go_back()
		"effect":
			main.fx_panel.set_running_by_name(w[1], true)
		"chase":
			for c in fx.chases:
				if c.name == w[1]:
					c.reset()
					c.running = true
		"fader":
			main.faders.set_level(int(w[1]), float(w[2]))
		"flash":
			if w[1] == "release":
				main.flash.release(main.flash.WHITE)
			else:
				main.flash.press(main.flash.WHITE)
		"blackout":
			main._on_blackout_all()
		_:
			printerr("unknown action: ", action)
