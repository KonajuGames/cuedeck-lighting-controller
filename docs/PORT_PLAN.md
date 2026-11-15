# Porting sLight to Cuepoint's framework: the plan

**Source:** `F:\Git\slight-dmx-controller`. A Godot 4.7 project with about 41k lines of GDScript (25k in the app, 16k in tests), an optional C++ GDExtension (`addons/native`) and a 300-maker fixture library in JSON.
**Framework:** `F:\Git\cuepoint-dj-controller`. C# on .NET 10, with its own retained-mode UI (`Brutality/UI`) drawn through Vulkan via the BRUTAL packages (GLFW, Vulkan, VMA, FreeType, ShaderC, SDL3), plus xUnit tests.
**Target:** this repository, `cuedeck-lighting-controller`.

## 1. What carries over and what doesn't

| sLight piece | Lines | What happens in the port |
|---|---|---|
| DMX core: `artnet_universe`, `artnet_sender`, `sacn`, `dmx_render`, `cue`, `chase`, `wave_effect`, `effects_engine`, `live_flash`, `palette_library`, `position_library` | ~2.2k | **Straight translation** to plain C# classes. Godot is only used here for `PacketPeerUDP` and `PackedByteArray`, which become `UdpClient` and `byte[]`. |
| Show model and persistence: `timeline_store`, `timeline_track`, `timeline_generator`, `autosave`, `app_settings`, `fixture_profile`, `fixture_library`, `fixture_import`, `mvr_io` | ~6.5k | **Translate.** Keep the JSON formats byte-compatible so existing `.slight` shows, profiles and timelines load (see §4). |
| Tempo, sound and triggers: `tempo_clock`, `sound_engine`, `sound_reactor`, `triggers_engine`, `trigger`, `osc`, `feedback_out`, `fft`, `spectrogram` | ~1.6k | **Translate the logic and replace the engine I/O.** Godot's `AudioEffectCapture` becomes a WASAPI capture (NAudio.Wasapi, the package Cuepoint uses; its `WasapiRecorder`) and `InputEventMIDI` becomes Cuepoint's `WinMidi`. |
| Song analysis: `song_analyzer`, `song_detect`, `song_analysis`, native `song_dsp` | ~1.3k + C++ | **Translate the structure, key and section detection.** For decoding, the beat grid and the FFT, **reuse Cuepoint's** `AudioDecoder` (SDL_mixer + Media Foundation), `BeatDetector`, `DownbeatDetector`, `KeyDetector` and `Fft`. That retires both the 4× capture-bus fallback and the C++ decoder. |
| DJ integration: `os2l_server`, `dj_performer`, `virtualdj_database`, `virtualdj_list`, `cuepoint_library` | ~2.3k | **Translate.** Cuepoint's `Os2l/*` is the client side and shares message parsing with the server. `cuepoint_library` can read Cuepoint's `LibraryFile` format with that project's own reader code. |
| Native GDExtension: USB DMX (FTDI D2XX/libusb), RtMidi out, mDNS advertiser, video recorder | C++ | **Drop the extension.** USB DMX: P/Invoke `ftd2xx.dll`/`libusb-1.0.dll` loaded dynamically, as the C++ did. MIDI out: `WinMidi.OpenOutput` (already exists). mDNS: extend Cuepoint's `MdnsBrowser` code with an advertiser. Video: Media Foundation `IMFSinkWriter` (H.264/MP4), done late. `tools/midi_bridge.py` is no longer needed. |
| UI panels: `dmx_controller` (shell), `universe_panel`, `cue_list_panel`, `chase_list_panel`, `effects_panel`, `sound_panel`, `groups_panel`, `faders_panel`, `fader_bank`, `palettes_panel`, `dmx_output_panel`, `triggers_dialog`, `options_dialog`, `music_browser_panel`, `music_timeline`, `position_editor`, `position_list`, `dj_perform_panel`, `structure_bar`, `wave_heatmap`, `xy_pad` | ~16k | **Rewrite** on Cuepoint's `Element` / `Root` / `Batch` UI. The behaviour is the spec; the code underneath changes completely (see §3). |
| 3D visualizer: `visualizer_panel`, `fixture_view` | ~1.7k | **Rewrite** with a new 3D Vulkan pass. This is the biggest new-engineering item (see §5). |

Cuepoint is Windows-only (WinMidi, Media Foundation, WASAPI, Win32 window chrome), while sLight also ran on macOS and Linux. **The port targets Windows only.** The platform-specific code stays behind small interfaces (`IMidiPorts`, `IPcmSink`, a capture interface), so another OS could be added later.

## 2. Project layout

```
Cuedeck.slnx
Cuedeck/                 app (WinExe, net10.0, AssemblyName "Cuedeck")
  Rendering/             copied from Cuepoint, plus Scene3D/ for the visualizer
  UI/                    framework widgets copied from Cuepoint, plus new widgets and the panels
  Input/                 copied from Cuepoint
  Dmx/                   universes, Art-Net, sACN, USB DMX, compositing
  Show/                  cues, chases, effects, palettes, positions, groups, show file
  Fixtures/              profiles, library, GDTF/QXF import, MVR
  Timeline/              DJ Edit timelines, generator, store
  Audio/                 decoding and analysis (from Cuepoint), live capture, sound reactors, tempo clock
  Dj/                    OS2L server, DJ performer, VirtualDJ and Cuepoint libraries
  Triggers/              MIDI/OSC triggers and feedback
  Shaders/               ui.vert/frag (copied) plus scene shaders
  Assets/                icons (redrawn with Cuepoint's draw_assets.py approach)
  Fixtures/lib/          the JSON fixture library, copied as content
Cuedeck.Tests/           xUnit
```

**Copy, don't reference.** Cuepoint's classes are `internal` to its exe, and both apps will change independently. Copy the framework files once (`Rendering/*`, `UI/{Elements,Root,Batch,FontAtlas,FreeType,Images,Png,Theme,ScrollPanel,ListView,TreeView,TextBox,Slider,Knob,Checkbox,Dropdown,ContextMenu,ConfirmDialog,NamePrompt,LevelMeter,TitleBar}`, `Input/*`, `WindowChrome`, `WindowsShell`, `ErrorLog`, `Midi/WinMidi`, `MidiMessage`, `Os2l/*`, and the `Audio`/`Analysis` pieces named in §1) under the namespace `Cuedeck`. Record the Cuepoint commit they came from (`6f1ee3e`) in a `FRAMEWORK.md`, so later fixes can be ported across by hand.

## 3. UI: mapping Godot controls to the framework

The framework already covers lists, trees, text boxes, sliders, knobs, checkboxes, dropdowns, context menus, confirm and name dialogs, scroll panels, tooltips and drag-and-drop. The port also needs these, which it doesn't have yet:

| Godot control (uses in sLight) | New element |
|---|---|
| `TabContainer` / `TabBar` (4 + the universe tabs) | `TabView`: a tab strip with closable and draggable tabs. Dragging a tab out is how the visualizer pops out. |
| `SpinBox` (20) | `NumberBox`: a TextBox with drag-to-scrub and step arrows. |
| `GridContainer` / `HBox`/`VBox` / `HSplitContainer` (≈30) | `Row`, `Column`, `Grid` and `Split` layout panels, if Cuepoint's hand-placed layouts don't generalise. Check `BrowserLayout.cs` first. |
| `VSlider` faders (4, plus the fader bank) | A vertical mode on `Slider`. |
| `ColorPicker` / `ColorPickerButton` | `ColorPicker`: an HSV square, a hue strip and a hex box, in a popup. |
| `FileDialog` (4) | The native Windows dialog (`IFileOpenDialog` via COM) in `WindowsShell`. No custom widget. |
| `Window` (4: the popped-out visualizer, the fixture library, triggers, options) | Modal dialogs as overlays in the one `Root`, as Cuepoint's options are. A **second OS window** for the popped-out visualizer needs a second swapchain on the shared `GpuContext`. |
| `ProgressBar`, `TextEdit` (1 each) | Small: a bar, and a multi-line mode on `TextBox`. |
| Custom-drawn controls (`xy_pad`, `structure_bar`, `wave_heatmap`, `spectrogram`, timeline lanes) | Direct ports onto `Batch`, which has rectangles, text and images. Waveforms follow Cuepoint's `ScrollingWaveform`/`WaveformBar`. The spectrogram and heatmap need a **dynamic texture upload** (add an `UpdateImage` path to `OverlayRenderer`), or they can be drawn as columns of rectangles. |

**Visual style:** use Cuepoint's `Theme` (near-black panels, orange accent) as the base. sLight's own colours where they carry meaning (the model colours, position hues, the dimmer-line blue) become named `Theme` entries.

**Input:** Godot's `_unhandled_key_input` shortcuts become a `KeyboardControls` class like Cuepoint's. MIDI that sLight took from `window_input` (so it's heard while a dialog is open) maps naturally: `WinMidi` callbacks are global already.

## 4. Data compatibility (do this early)

- **Show file (`.slight`, JSON):** write `ShowFile` DTOs with `System.Text.Json`. Godot's JSON parses every number as a float and writes integers as `1.0` in places. Read numbers leniently, and write what Godot's `JSON.parse_string` accepts. Colours and vectors are stored as arrays and dicts by sLight's own `to_dict()`; mirror each one exactly.
- **Golden files:** export a handful of real shows from sLight, covering every feature tab, timelines and positions. A round-trip test (load → save → compare as JSON trees) gates every phase.
- **`user://` → `%APPDATA%\Cuedeck`:** settings, autosave, session, fixture profiles and models, last show and UI state. Offer a one-time import from Godot's `%APPDATA%\Godot\app_userdata\sLight` folder.
- **Fixture library:** `fixtures/index.json` and `fixtures/lib/*.json` are copied as-is. `tools/build_fixture_library.gd` becomes a small `dotnet run` tool, or stays in the Godot repo.

## 5. The 3D visualizer

Today it gets Godot's Forward+ for free: spotlights per fixture head, volumetric fog for beams, optional shadows, glTF bodies from GDTF, orbit camera, picking, drag-to-move, trusses, rooms, camera views, undo/redo and MP4 recording. The port adds a `Rendering/Scene3D` pass that draws into an offscreen image, which the UI shows as an `Element`. That keeps the popped-out window and video capture simple.

Build it in steps, each shippable:

1. **Geometry and camera:** a depth buffer, one lit opaque pipeline (Lambert plus ambient and the work light), procedural floor, wall, truss and schematic fixture bodies by category (box, yoke, bar), orbit/pan/zoom camera, and picking by ray against bounding boxes. Fixtures and trusses can be placed and moved, with undo.
2. **Lights:** each fixture head is a spotlight (colour, intensity, cone from zoom, direction from pan/tilt). The opaque shader loops over a storage buffer of lights, with no shadows. The light count (heads × fixtures) can reach hundreds, so start with a flat loop and add tiled culling only if profiling says so.
3. **Beams (instead of volumetric fog):** draw each beam as an additive cone mesh. Its brightness falls off with distance and with how edge-on the view is, scaled by the haze slider; gobos fade it with a texture. This is the standard look in lighting visualisers, and far cheaper than froxel fog. Real ray-marched fog is optional later.
4. **GDTF models:** load the glTF parts with SharpGLTF (NuGet) and drive the pan/tilt axes from the profile's geometry tree, falling back to the schematic bodies.
5. **Extras:** shadows (one shadow map for the few beams the user picks), MP4 recording through the Media Foundation sink writer, and a pop-out window.

`DmxRender` (pure: DMX in, `{dimmer, pan, tilt, heads[]…}` out) ports first and unchanged. Its tests carry over, and the renderer just consumes its output.

## 6. Threading and the frame loop

Godot ran `_process`, a refresh `Timer` and worker `Thread`s. The new layout:

- **DMX tick thread:** a fixed rate, 30 Hz as sLight's refresh timer runs. It runs `Tempo`, `Fx`, `Triggers` and `DjPerformer`, composites the layers (playback → effects → DJ timelines → live flash top layer → grand master), then transmits.
- **UI/render thread:** the GLFW loop, as Cuepoint's. It reads the latest composited output from a lock-free double buffer.
- **Workers:** song analysis and fingerprinting, using Cuepoint's `BackgroundWork` pattern.

Godot signals become C# events. Every handler that touches UI state marshals to the UI thread through a queue drained each frame, as Cuepoint does for MIDI.

## 7. Phases

Each phase ends with the app building, the ported tests passing, and the golden shows round-tripping.

| # | Phase | Done when |
|---|---|---|
| 0 | ✅ **Scaffold** (done 2026-11-13). Solution, framework files copied and renamed, an empty window with title bar and theme. Test project added. `README`/`CLAUDE.md` describe the build. | `dotnet build` and `dotnet test` pass; a window opens. |
| 1 | ✅ **Headless core** (done 2026-11-13; see §7.1). Universes, Art-Net/sACN, compositing, cues and crossfades, chases, wave effects, palettes, positions, groups, virtual dimmers, live flash, tempo clock, and the show file and fixture profiles. Port `dmx_render`, `cue_timing`, `virtual_dimmer`, `faders`, `palettes`, `position`, `live_flash`, `tempo_clock`, `show_file`, `fixture_profile`, `autosave` and `unsaved_changes` tests to xUnit. | Real Art-Net out from a loaded show, driven by a test harness, matches sLight's output frame for frame (compare by capturing UDP from both). |
| 2 | ✅ **Console UI** (done 2026-11-13; see §7.2). Shell (top bar, grand master, run mode, Sending, universes, save/load), Patch/universe tabs with the fixture library dialog, then Cues, Chases, Effects, Groups, Faders/fader bank, Palettes and DMX Output. New widgets from §3 as each tab needs them. | Every console tab works for the sLight User Guide walkthrough. |
| 3 | ✅ **Sound and triggers** (built 2026-11-13, see §7.3; not yet tried with a real line-in or controller). Live capture, sound reactors and the Sound tab with spectrogram. MIDI/OSC triggers with Learn and feedback (WinMidi out). Options dialog (VirtualDJ or Cuepoint). | Beat-reactive chases run from line-in; a controller's LEDs follow. |
| 4 | ✅ **DJ Edit Mode** (done 2026-11-14; see §7.4). Music browser (drives, VirtualDJ lists, Cuepoint library), song analysis, the structure bar, heatmap and timeline editor, timeline generator, timeline store and the position editor. Port `music_*`, `timeline_*`, `structure_detection`, `virtualdj_*`, `cuepoint_library` and `m4a_playback` tests. | Generate → edit → save a song's timelines, and the files load in sLight too. |
| 5 | 🔶 **DJ Perform Mode** (built 2026-11-14 and tried with Cuepoint; VirtualDJ still to try; see §7.5). OS2L server with mDNS advertising, the DJ performer mixing deck timelines, and the perform panel. Port `os2l_server` and `dj_perform` tests, and test against Cuepoint itself as the OS2L client. | Cuepoint and VirtualDJ drive the lights live. |
| 6 | ✅ **Visualizer** (done 2026-11-14; see §7.6). §5 steps 1–3, then 4. Port the `visualizer_undo` and `fixture_view` tests. | The rig in `docs/img/09-visualizer.png` looks comparable. |
| 7 | ✅ **Native extras and polish** (done 2026-11-15; see §7.7; USB DMX not yet tried with an interface). USB DMX (FTDI/libusb P/Invoke), MP4 recording, visualizer pop-out window and shadows, MVR import/export, screenshot capture tool, user guide and README rewritten with new screenshots. | Feature parity checklist (from sLight's README) all ticked. |
| 8 | ✅ **Parity gaps** (done 2026-11-15; see §7.8; libusb not yet tried with an interface). The two ❌ in `docs/FEATURES.md`: bloom on bright beams in the visualizer, and USB DMX through libusb (the anyma uDMX, and FTDI interfaces without the D2XX driver); then importing sLight's own files. | `docs/FEATURES.md` has no ❌. |

### 7.1 Phase 1 as built

**Where it is:** `Cuedeck/Common` (Godot-style rounding, colours with Godot's colour names, lenient JSON), `Cuedeck/Fixtures` (profiles, built-ins, `DmxRender`), `Cuedeck/Dmx` (universes, Art-Net and sACN packets, `DmxOutput` compositing and crossfade), `Cuedeck/Midi/MidiMessage.cs`, and `Cuedeck/Show`. `LightingShow` wires together everything sLight's shell wired together (patch, groups, cue list, chases, effects, reactors, palettes, positions, faders, live flash). `ShowFile` reads and writes sLight's format, and `ShowSession` covers the current file, unsaved changes, the remembered show and autosave recovery.

**How it was checked:** 73 xUnit tests, ported from sLight's `dmx_render`, `virtual_dimmer`, `tempo_clock`, `live_flash`, `palettes`, `cue_timing`, `faders`, `position`, `show_file`, `unsaved_changes`, `autosave` and `fixture_profile` tests, plus packet-layout tests. The phase's "done when" is met with a golden scenario instead of UDP capture. `Cuedeck.Tests/Golden/scenario.slight` is a two-universe show built by Cuedeck, and `tools/slight_frames.gd` plays a 320-frame script on it in sLight 1.0 (`406366d`, Godot 4.7) and records the output. `SlightOutputTests` plays the same script in Cuedeck and requires every frame to match byte for byte. It does, across cues, delays, follows, palettes, chases, effects, faders, virtual dimmers, a live flash, the grand master and a blackout. sLight loading a show written by Cuedeck is part of that check too. Packets are compared by layout rather than captured, since the frame is all that varies.

**Left for later phases, on purpose:**
- Fixture library, GDTF/QXF import and MVR (`fixture_library`, `fixture_import`, `mvr_io`, and those parts of `fixture_profile_test`) go to phase 2, with the Patch tab that uses them.
- MIDI/OSC triggers and the visualizer's stage are carried through show files unchanged (`LightingShow.Carried`), so nothing is lost. They're ported in phases 3 and 6.
- Live audio: reactors run against `ISoundLevels`, which is silent until phase 3.
- USB DMX: `DmxOutput.SendUsb`/`ReleaseUsb` hooks only (done in 7d).
- Fixture highlighting and aiming for the position editor go to phase 4.
- Nothing is on screen yet: the window doesn't create a `LightingShow` or tick it until phase 2.

**Where Cuedeck deliberately differs:**
- Patching a fixture homes that fixture only. sLight rebuilt every fixture's row, which homed them all.
- `LightingShow.Retarget()` finds effects' and reactors' channels again after a show loads. In sLight the starter reactors had none until their tab was touched.
- Where the cue list is (its live and next cue) is saved with the show but doesn't count as an unsaved change, so a GO doesn't mark the show unsaved. In sLight it did.
- Renaming a group renames it in the effects, reactors and group faders that use it. In sLight they kept the old name and stopped finding the group.
- A fader lets go of the chase or effect it started when it comes down (or is removed), even if that's been renamed meanwhile; and of one another fader is also running, only when the last of them comes down. sLight looked it up by name again, so a renamed one ran on.
- MIDI Learn on the Faders tab waits for MIDI input (phase 3): for now a fader shows the mapping a show brought and can forget it. (Learn came with slice 3b.)

### 7.2 Phase 2 progress

Phase 2 is done in slices, each leaving the app working.

| Slice | What | State |
|---|---|---|
| 2a | The show running in the app (30 Hz tick, Sending, autosave every minute, crash recovery, reopening the last show or one given on the command line, unsaved-changes prompts on New, Load and close); new widgets (`TabView`, `NumberBox`, `Row`, `Flow`, `Region`/`ScrollingPage`, lit buttons, wrapping labels); the top bar (grand master, run mode, Tap and tempo, Sending, Blackout All, Add and Remove Universe, New, Save, Save As and Load with Windows' own dialogs, and their shortcuts); the console's layout; the Patch tab (connection settings, Blackout Universe and Universe Full, adding fixtures from the built-in and custom profiles, fixture rows with category, category group, Leave out, Home, Remove, and controls: virtual dimmer, colour swatches, pan/tilt pad, 16-bit sliders, slot pickers, sliders) | ✅ |
| 2b | Cues tab (GO, Back, Halt, the progress bar, the list with the live cue and palettes used, editing the selected cue's label, fades, delay, follow and tracking, Load to Patch, Update, Duplicate, Delete, double-click to fire, Record Cue with its fade and the Tracking switch) and Space for GO | ✅ |
| 2c | Chases tab (new and delete, run, tempo, crossfade, direction, beat sync, steps recorded, updated, loaded to the patch and deleted); Effects tab (new and delete, run with its channels found in the patch, role, universe or group, waveform, base, rate capped for motors, tempo beats, size, centre, fan, phase, rebuilding targets); Groups tab (new and delete, renaming that carries the effects, reactors and group faders on it along, a checklist of every patched fixture) | ✅ |
| 2d | Faders tab (a strip per fader: name, remove, the fader and its level, kind and target with missing targets marked, its MIDI mapping and Forget); Palettes tab (Fixtures: all or a group, Record per kind, palette buttons in their colour that apply on click, and Update from Live, Rename and Delete on right-click, deleting baking into the cues); DMX Output tab (a 512-channel grid per universe, lit by value and tinted by fixture model); fixture rows in their model's colour | ✅ |
| 2e | The bundled fixture library (638 fixtures from the Open Fixture Library, copied from sLight with its attribution) and its dialog (search, manufacturer, type, Pan/Tilt and channel filters, a preview of each mode's channels, Add to Patch); a colour picker for the fixture swatches; the patch's Flash (hold for full white); Save and Load Preset | ✅ |
| 2f | Profile import from files (GDTF, with its gobo pictures, geometry and 3D models, and Open Fixture Library JSON; sLight has no QXF import, so neither does Cuedeck); the profile editor (New, Edit, Delete or reset an edited built-in, saved to the profiles folder; an edit goes to the fixtures patched with the profile); the library's online sources (the current OFL from GitHub, and GDTF-Share, whose login is kept in memory only, each fixture downloaded when it's chosen and cached) | ✅ |

**Where slice 2f differs from sLight:**
- The editor's head positions are on one line, split by "/", as Cuedeck's text boxes are single-line. sLight had a box with a line per head.
- Channels in the editor move with ▲ and ▼ buttons rather than by dragging their handle.
- GDTF-Share's login is a box over the library dialog rather than a window of its own; Escape or Cancel goes back to the bundled library, as sLight's did.
- An imported gobo picture is shrunk with Cuedeck's own bilinear resize, so its pixels can differ slightly from Godot's. Head rotations are worked out in doubles where Godot used floats.

What runs on the UI thread for now: the show ticks in the main loop, so its output pauses while one of Windows' file dialogs is open (Art-Net and sACN receivers hold the last frame). Moving the tick to its own thread (§6) comes with the locking that needs.

### 7.3 Phase 3 progress

| Slice | What | State |
|---|---|---|
| 3a | Live audio: WASAPI input (`Audio/WasapiInput`), the analysis Godot's spectrum analyser did (a 2048-point FFT of the latest samples, the loudest bin in each band, -60 to 0 dB as 0 to 1, times the gain), sLight's smoothing and beat detection (`Audio/SoundEngine`), listening only in Sound Reactive, beats stepping beat-sync chases and pulsing reactors; the Sound tab (input and Rescan, band meters, spectrogram, gain, beat sensitivity and response, the reactors and their settings) | ✅ |
| 3b | MIDI input from every connected device (Cuepoint's `WinMidi` and `MidiDevices`, rescanned every two seconds); MIDI and OSC triggers (`Triggers/Trigger`, `TriggersEngine`, `Osc`: notes, CCs, program changes and pitch bends, or OSC addresses, firing cue GO, Back, Halt and Go To, chase and effect toggles, Blackout, Tap tempo and held flashes) saved with the show; the Triggers dialog (MIDI and OSC input, bindings, Learn, the last message heard) from a Triggers… button on the top bar; MIDI clock to the tempo clock; MIDI Learn on the Faders tab | ✅ |
| 3c | Feedback to controllers: a binding's pad lit from its cue, chase, effect or flash, the beat, Sending, anything running or the run mode, sent again every two seconds, through a chosen MIDI output (`WinMidi`), else sLight's UDP MIDI bridge, or as OSC; the Triggers dialog's feedback settings; the Options dialog from the title bar's cog (VirtualDJ or Cuepoint, kept in `settings.json` as sLight's `AppSettings`) | ✅ |

**Where slice 3c differs from sLight:**
- MIDI feedback goes straight to a MIDI output through winmm, so the MIDI output picker always shows (sLight's needed its native extension built). With no output chosen it still goes to `midi_bridge.py` over UDP, as sLight's did without the extension.
- The MIDI inputs aren't opened with their outputs (Cuepoint's `MidiDevices` does), leaving the output free for feedback, as some drivers let only one program open a port.
- Options opens from the title bar's cog rather than an Options… button on the top bar.

**Where slice 3b differs from sLight:**
- The Triggers dialog's feedback settings (per binding and the outputs) wait for slice 3c; they're read from and saved to shows meanwhile.
- MIDI comes from every input through winmm, read each frame, rather than through Godot's MIDI input; sLight also read it once a frame.
- Learn is a button that lights while it listens (click again or Escape to stop), rather than a toggle button.
- A fader strip keeps a Forget MIDI button beside Learn, as well as forgetting on a right-click as sLight's did.

**Where slice 3a differs from sLight:** the analysis runs at the show's 30 Hz tick, and on the latest 2048 samples rather than the frame Godot's analyser picked for its output latency, so levels can differ a little from sLight's for the same sound. Live audio can't be compared frame for frame anyway; the smoothing and beat rules are sLight's.

### 7.4 Phase 4 progress

Phase 4 is about 12,000 lines of sLight's GDScript and 5,000 of its tests, so it's done in slices from the data
outwards, each slice's tests ported before its code.

| Slice | What | State |
|---|---|---|
| 4a | The music libraries: VirtualDJ's databases (`database.xml`: tags, analysis, variable-tempo beat grids, cue, automix and remix points, merged across drives) and lists (`.vdjfolder`, in its `order`), and Cuepoint's library files (`library.json`) and virtual folders (`.m3u8`), all as `Dj/DjSong` and `Dj/DjList` | ✅ |
| 4b | Lighting timelines as data (`Timelines/`): a track's rows (the dimmer line, and colour, strobe, position and movement spans) and their edits (`timeline_track`'s model), saved as sLight saved them (`TimelineFormat`); the timeline store beside a show, with sLight's song fingerprints (checked against Godot's); the selection | ✅ |
| 4c | Song analysis (`Audio/`): decoding (Cuepoint's `AudioDecoder`: SDL_mixer and Media Foundation; sLight's WAV reader), sLight's native front end (`song_features.cpp`) and its detection (`song_detect`: tempo, DP beats, downbeat, key, structure), `SongAnalysis` saved as sLight saved it | ✅ |
| 4d | The timeline generator (`Timelines/TimelineGenerator`, on Godot's random numbers in `Common/GodotRandom`), checked node for node against sLight's own shows, and regenerating a list of songs' timelines (`TimelineBatch`) | ✅ |
| 4e | DJ Edit Mode's timelines without the screen (`Timelines/SongTimelines`, `MusicTimeline`'s engine half): the fixtures' tracks, selecting by row and time, Delete, Cut, Copy, Paste, dragging a selection and its edges, the rows' menus' edits, mute, solo and collapsing, saving as songs are left, undo and redo per song, the structure cache and its analysis, VirtualDJ's grid on it, Generate (into a selection too), Shift+Generate, and generating into songs not open (for `TimelineBatch` and DJ Perform Mode) | ✅ |
| 4f | The timeline's screen (`UI/Timeline`): the waveform and overview (`WaveView`, `ViewBox`), the bar ruler, the structure bar and its edits (`StructureEdits`), the tracks with their name plates (`TrackView`, `NamePlate`), the pointer and keys onto `SongTimelines`, the context menus, middle-button panning, and playback (`Audio/SongPlayer`, `WaveformEnvelope`) | ✅ |
| 4g | The music browser (`Music/MusicBrowser`, `MusicFolders`, `DjLibrary`; `UI/DjEdit/DjEditView`): drives and folders, the DJ software's lists, the track list with its timelines marks, Regenerate and Remove Lighting Timelines; DJ Edit Mode on the top bar's View dropdown; each show's timelines folder following its file (`ShowSession`) | ✅ |
| 4h | The named positions in DJ Edit Mode (`UI/DjEdit/Positions`): the Positions list under the folders (add, pick, rename, duplicate, delete), and the editor (the pan / tilt pad, the fixtures with pan and tilt, copying and pasting points, Snap Together, Spread and Reset); the selected fixtures lit full white and pointed at the position (`Patch.SetHighlight`, `Aim`), a show saved meanwhile keeping their real levels | ✅ |

Phase 4's done-when, checked: a song's timelines generated and edited in Cuedeck, saved, then read by sLight's own
`TimelineStore` and `MusicTimeline` row readers in headless Godot: its index found the song by path and every row
read back, down to a movement's phase.

**Where slice 4h differs from sLight:** the editor and list are drawn by Cuedeck rather than styled as Godot's
`ItemList`; a position's triangle is drawn with the rest rather than made as an icon, so the timeline's Position menu
has none. Its delete button is "×" (the fonts Cuedeck bakes have no "✕"). sLight played the open song's timelines
into the rig while DJ Edit Mode was playing (through DJ Perform Mode's output, `DjPerformer.song_output`); that comes
with DJ Perform Mode in phase 5. Fixed on the way: the list's heading refresh from 4g closed the editor on the view's
first frame.

**Where slice 4g differs from sLight:** the Positions section of the left column (and its editor) comes in 4h; until
then Generate uses the show's named positions as they are (making a "Centre" one when there are none, as sLight did).
The track list sorts by sLight's columns but doesn't let a column be hidden, and selects whole rows already (sLight
had to make Godot's cell selection whole). Copy Path and Show in File Explorer go through the app's clipboard and
Explorer. sLight's own test of real VirtualDJ lists on its machine isn't ported; the tests stand in a library. Tried
in the running app: a real MP3 decoded and its waveform drawn (the first real MP3 since 4c), its structure analysed,
playback moving the playhead through SDL_mixer (not listened to), and Generate filling the master's rows.

**Where slice 4f differs from sLight:** the screen (`TimelineView`) only went on show with the browser in 4g, and `SdlSongPlayer` (SDL_mixer on the default
output device) hasn't played through real speakers; the tests play on a fake clock (`FakePlayer`). A song is
decoded once, for both its waveform (`WaveformEnvelope`, sLight's native `waveform_file`, ported) and playback; sLight
read the waveform natively and decoded M4A again for playback. Its structure is still analysed from the file
separately (`SongAnalyzer`). The context menus have no separators, shortcut hints or icons (sLight's colour swatches
and position triangles), and Static Colour… and Gradient… use the app's colour picker: the selection follows it live
as one undo step, a gradient picking its start and then its end. Shift-dragging a dimmer node locks the axis if
Shift was held as it was pressed (Cuedeck's pointer drags don't carry the keys). A position's colour and the "Centre"
position made for Generate come with the position list in 4h. sLight's tests on real files (M4A timing against
VirtualDJ's grid, variable-tempo grids, a long mix cancelled) aren't ported, as in 4c, and nor are those of its
Godot-specific details (embedded subwindows, the cursor shape over a node, drawing only what's scrolled into view).

**Where slice 4e differs from sLight:** sLight's `MusicTimeline` was one Godot control that drew the timelines and
took their input as well as holding their edits; here `SongTimelines` holds the edits and the screen in 4f will drive
it. So its selection drags take a row (of `RowList()`) and a time rather than a point on screen, and a press checks
for a selection's edge within a time the screen works out from its pixels. It takes the song's length from the screen
once the waveform's read (`SetDuration`), where sLight read it off its waveform view, and takes in finished structure
analyses and background saves in `Update()`. The patch comes in as `PatchedFixture`s, and the named positions as the
generator takes them (the position library, which made a "Centre" one when there were none, comes in 4h). The tests
port the engine half of `timeline_track_test.gd` (fixture tracks, selecting, Delete, copy and paste, undo, the rows'
menus, dragging, the strobe, position and movement rows, inheritance, mute, solo and collapsing), the autosave half
of `timeline_store_test.gd`, `music_timeline_test.gd`'s VirtualDJ grid and `music_browser_test.gd`'s regenerating
checks on the timelines' side; what those tests check of menus, cursors, plates and drawing comes with the screen.

**Where slice 4d differs from sLight:** none in what it makes: `SlightGeneratorTests` checks 16 shows (plain rigs,
fixture categories and groups, multi-head movers, colour wheels, with and without a song's loudness, another bar
phase) against what sLight's `TimelineGenerator.generate()` made for the same input, recorded by
`tools/slight_generator.gd`, to the last bit. That took Godot's random number generator and string hash
(`GodotRandom`) and its 32-bit `Color.from_hsv()` and `lerp()` (now `Rgb.FromHsv` and `ColourSpan.Lerp`). The
generator takes its tracks, positions and envelope as records rather than dictionaries. `TimelineBatch` hands its
results on in `Update()`, called each frame, where sLight used signals; it reaches DJ Edit Mode (its structure cache
and `generate_into()`, which `SongTimelines` has from 4e) through `ISongTimelines`. sLight's check of a position's
spread came with 4e (`SongTimelines.Spread`); its batch checks that drive the music browser's menu and bars come with
the browser in 4g.

**Where slice 4c differs from sLight:** only sLight's fast (native) analysis is ported; its slow path, which played the song muted to capture it, isn't needed. Decoding playback (`m4a_playback_test`) comes with the player in 4e. The tests use WAV only; MP3, OGG and M4A decoding through SDL_mixer and Media Foundation hasn't been tried on a real file yet, and sLight's real-file structure checks aren't ported.

**Where slice 4b differs from sLight:** `TimelineTrack` is the data alone; sLight's was a Godot control that drew itself and took the mouse. Its edits take times and levels rather than pixels (the screen in 4e turns one into the other), and most of `timeline_track_test.gd`, which drives the timeline's screen, comes over with it. So does the half of `timeline_store_test.gd` that saves as songs are opened and left.

**Where slice 4a differs from sLight:** sLight's tests also loaded the VirtualDJ and Cuepoint libraries on the machine running them, as a check; those parts aren't ported, as Cuedeck's tests don't depend on what's installed.

### 7.5 Phase 5 progress

| Slice | What | State |
|---|---|---|
| 5a | The OS2L server (`Os2l/Os2lServer`): JSON objects back to back over TCP, split on bytes with braces and quotes in strings handled; beat, button and command events, VirtualDJ's subscribed values (kept, raised only when they change), feedback sent back; and mDNS advertising (`_os2l._tcp`) through Windows' own DNS-SD responder (`Os2l/MdnsAdvertiser`, dnsapi.dll), a loopback-only listener not advertised | ✅ |
| 5b | The DJ performer (`Perform/DjPerformer`): VirtualDJ's decks followed over OS2L (the song, play, the volume fader, the position run on between reports at the rate they advance, the skin's deck count), each song's timelines read on a worker thread and played into a layer of the DMX output (`Perform/SongOutput`: the master, fixture and head rows, mute and solo, positions and movement, strobe speeds, colour wheels, fixtures left out of generation), the decks mixed highest-takes-precedence with movement averaged, and VirtualDJ's beats driving the tempo clock; temporary timelines for a song without any (a stand-in on the beat, then a show generated once it's analysed, saved flagged temporary); and DJ Edit Mode's open song played into the rig (`Perform/EditPlayback`) | ✅ |
| 5c | DJ Perform Mode's view (`UI/DjPerform/DjPerformView`), chosen from the View dropdown, which starts the performer and stops it when another view's chosen: the connection (listening, advertising, the DJ software connected), the live flashes (held buttons and B / S / W / C, Latch, the Colour flash's colour) and each deck's state, song, position and timelines | ✅ |

**Tried live (slice 5c):** Cuedeck in DJ Perform Mode and Cuepoint (`6f1ee3e`) on the same machine. Cuepoint found
Cuedeck by mDNS and connected by itself; a song loaded on its deck A showed on deck 1 at once, paused, with temporary
timelines generated for it (not saved, the show being new); played with the deck's fader down it showed "playing
(muted)" with the position running on. Not yet tried: VirtualDJ, and a rig. Cuepoint doesn't answer the `get_loop`
subscription (it says so on its status line); nothing uses it yet.

**Where slice 5c differs from sLight:** the view is redrawn each frame rather than refreshed ten times a second. Typing
in a text box no longer sets off the app's single-letter shortcuts (the flashes' B / S / W / C, and Tap's T, which
did before): Godot's text fields kept those keys from sLight's shortcuts, Cuedeck's left them unused. Like sLight it
names the DJ software chosen in the Options ("VirtualDJ connected") whichever program connects.

**Where slice 5b differs from sLight:** a deck's tracks are `TimelineTrack`s rather than dictionaries of rows; loads
and generating hand their results back through a queue that `Update()` empties each frame, where sLight deferred
calls to the main thread. The performer is made in `Program` but nothing starts it until 5c's view; DJ Edit Mode's
playback is live from now on. The panel's half of `dj_perform_test.gd` comes with the panel, and its check that
DJ Edit Mode's playback goes live drives the engine with the screen's state rather than the screen.

**Where slice 5a differs from sLight:** the server is polled by whoever owns it, raising C# events where sLight's
node emitted signals from `_process()`. Advertising goes straight to dnsapi.dll from C#, where sLight needed its native
extension built (so "native extension not built" can't happen); the macOS and Linux responders it also had aren't
ported, so there it reports no DNS-SD service and points at VirtualDJ's `os2lDirectIp`. A client that sends 1 MiB
without finishing a message is dropped. The tests advertise through a fake responder on a loopback listener, so they
don't touch the network or bring up the firewall's prompt; the real registration was checked once by hand, Cuepoint's
own mDNS browser finding it on the right port and stopping withdrawing it.

### 7.6 Phase 6 progress

sLight's visualizer is `visualizer_panel.gd` and `fixture_view.gd` (1,700 lines) over Godot's renderer, so it's done
from the scene's data outwards: first everything that isn't drawing, then §5's steps.

| Slice | What | State |
|---|---|---|
| 6a | The scene without the GPU (`Visualizer/`): the room, haze, work light and shadows settings; the orbit camera and the saved views; trusses and models; picking by ray and dragging on the floor; selection, typed positions and turns, Auto-arrange and the undo history; saved with the show as sLight's `viz`; and each fixture's rig (category, yoke or fixed, its heads, pan and tilt eased towards its DMX, the beam's angle) | ✅ |
| 6b | The 3D pass (`Rendering/Scene3D`): an offscreen colour and depth image shown in the 3D Visualizer tab, one lit opaque pipeline (Lambert, ambient and the work light), the floor, wall, trusses and schematic bodies; the camera, picking and dragging by mouse; the toolbar and the selected item's properties | ✅ |
| 6c | Lights: each lit head a spotlight (`SceneFrame.Lights`: its colour times sLight's energy and its level, its cone from the beam's angle, aimed by the rig), looped over in the opaque shader with Godot's fall-off with distance and towards the cone's edge, and faded out 24 m to 34 m from the camera as sLight's were | ✅ |
| 6d | Beams: sLight's open cones from the lit heads that throw one (as wide as the beam's angle, 13 m long, fading from the lens), added over the room by a second pipeline and shown more in thicker haze; blinders' and bars' heads as lit faces glowing in their colour; the strobe blinking them all (from the rig); gobos projected by the first head's light across its cone, turned by the gobo's spin (`Rendering/GoboArray`, the artwork's layers in one texture array) | ✅ |
| 6e | GDTF models: a profile imported from GDTF with its models drawn from its geometry tree (each part's glTF model in its model's colour, panning and tilting on the geometries its Pan and Tilt channels name, a head at each beam), else schematically; glTF models placed as set pieces (Load Model…), drawn in their own colours and picked by the box round them; a glTF reader of Cuedeck's own (`Visualizer/GltfModel`) | ✅ |

**Where slice 6e differs from sLight:** glTF is read by a reader of Cuedeck's own (binary and JSON, embedded or
side-by-side buffers; meshes, node transforms and base colours, no textures) rather than SharpGLTF as §5 planned, so
nothing new is downloaded. A GDTF fixture whose models can't be read (another machine without its
`fixture_models`) is drawn schematically, where sLight drew its geometry without bodies. Models aren't textured: a
fixture's parts take its model's colour, as sLight tinted them; a set piece its materials' colours. Tried in the app:
a set piece (a cube made for the purpose) loaded through Load Model…'s file dialog, drawn and selected. No GDTF
fixture with models was at hand (none is imported on this machine), so those are tried by the tests only.

Phase 6's done-when: the rig drawn with its beams, pools of light and haze looks comparable to sLight's
`09-visualizer.png`; anti-aliasing, shadows, recording and the pop-out window are phase 7's.

**Where slice 6d differs from sLight:** the beams are sLight's cones without Godot's volumetric fog behind them, so
the haze slider scales the cones themselves (as sLight drew them at its usual 0.3) as well as the veil. A gobo is
projected in the light's shading, sampled across the cone, rather than as Godot's projector texture; up to 16
different gobos at a time. Tried in the app: two pars' beams down to their pools on the floor and a mover's across
the room; a gobo on screen wasn't checked (the tests cover where it goes and how it turns).

**Where slice 6c differs from sLight:** every lit head's light is looped over for every pixel, with no culling (a
few hundred lights is fine at this size); no shadows. Godot's energy units are kept, scaling a spot's energy by pi
as Godot does, so its light on the floor is as strong as sLight's. Tried in the app: two RGB pars and two moving heads
at full, the pars' pools of light on the floor under them.

**Where slice 6b differs from sLight:** the scene is drawn into an image of its own each frame
(`Rendering/Scene3D`: one pipeline, Lambert under the ambient light and the work light) that the UI shows as a
second texture, where Godot rendered a `SubViewport`. The haze is a veil thickening with distance, lit by the same
light, standing in for Godot's volumetric fog until the beams come in 6d; highlights are rolled off rather than
ACES-mapped, which darkened the room well below sLight's. No MSAA yet. The fixture's selection ring and a selected
truss glow cyan as sLight's did; a set piece is a grey box until its model's read (6e). Load Model…, Import and Export
MVR…, Screenshot and Record come with 6e and phase 7, and the Shadows switch is kept and saved but does nothing yet.
Tried in the app: two moving heads and two RGB pars patched, the room, wall and fixtures drawn at sLight's scale and
tone, a fixture picked with the mouse and its properties shown, the views switched.

**Where slice 6a differs from sLight:** the scene is an engine class (`VizScene`, on `LightingShow.Visualizer`) that
the screen will drive, where sLight's panel held it in its nodes; the fixtures' places stay on the patch as before.
Picking is a ray against each item's box, where sLight asked Godot's physics. A set piece whose model file isn't there
is kept (and saved again) rather than dropped on load, and a new or loaded show starts from the default scene before
its `viz` is read, where sLight kept the last show's room and camera for anything a file left out. sLight's
`visualizer_undo_test.gd` is ported but for its Ctrl+Z / Ctrl+Y and Enter-in-a-field checks, which come with the
screen in 6b. The golden scenario now carries a full `viz`, and sLight, reading it, plays the same frames.

### 7.7 Phase 7 progress

sLight has no tests of its own for these, so each slice's are new, written from its code.

| Slice | What | State |
|---|---|---|
| 7a | MVR (My Virtual Rig) import and export (`Fixtures/Mvr`): a rig's fixtures read from its scene description with their GDTFs, addresses and places, its trusses as the visualizer's; and the patch written out as a scene description with a GDTF made for each profile; from the visualizer's toolbar | ✅ |
| 7b | The visualizer's picture: anti-aliasing (4× MSAA), Screenshot (a PNG of the 3D view), and Record (the view to an MP4, H.264 with what the computer's playing as AAC, through Media Foundation's sink writer) | ✅ |
| 7c | The visualizer's pop-out window (`VizPopOut`: a window of its own on the same GPU, its tab saying where it's gone, docked again by closing it or Dock to Main, where it was kept in `ui_state.json`), and shadows: the brightest eight lights each casting one from a shadow map, their edges filtered | ✅ |
| 7d | USB DMX (`Dmx/UsbDmx`): Enttec Open DMX and DMX USB Pro (FTDI's D2XX, P/Invoked), a universe's USB output with its interface and kind chosen on the universe's page | ✅ |
| 7e | Remembered folders for every file dialog (`RecentFolders`, sLight's `RecentDirs`: the show, fixture import, glTF model and MVR dialogs each start where they were last used); and what the feature check against sLight's README turned up: a universe whose USB interface won't open goes back to Art-Net, and the pan / tilt pad's wheel nudge | ✅ |
| 7f | The README and user guide rewritten for Cuedeck (`README.md`, `docs/USER_GUIDE.md`), with new screenshots taken by the app itself (`--capture`: `ScreenshotTool`, a demo rig and made-up songs, each frame read back from the GPU by `Renderer.CaptureWindow`), and sLight's feature list ticked off (`docs/FEATURES.md`) | ✅ |

**Where slice 7f differs from sLight:** sLight's `tools/capture_screenshots.gd` took eleven of the guide's pictures
and left DJ Edit Mode's four to be taken by hand with a real song; Cuedeck's tool takes all fifteen and the README's,
with synthesised songs (`DemoSongs`) in a demo folder and a made-up library standing in for the DJ software's. It runs
on a scratch app folder (`AppPaths.UseFolder`), so the user's shows, settings and music are never read, and saves the
window's own frame, never the screen. The guide adds a Palettes picture (07, a number sLight's guide had left
unused). Two things the screenshots showed were fixed: fader strips of different heights now line up along the top
(`Flow.AlignTop`), and DJ Edit Mode's open-song row cuts a long title short rather than running past its column. The
feature check found one gap not recorded before: Godot's bloom on bright beams isn't ported.

**Where slice 7e differs from sLight:** none in what's remembered: each dialog's folder is kept by the same keys
in `recent_dirs.json` in the app's folder. The show dialogs start in the current show's folder when they've no
folder of their own yet. The feature check went through the README's console, playback, triggers, palettes, faders,
profiles, visualizer and output sections. Two gaps were fixed. A USB interface that won't open now leaves its
universe on Art-Net with no interface named, as sLight did. The pan / tilt pad's wheel now nudges a step (Shift for
pan). Differences kept on purpose: the Groups tab's checklist follows the patch by itself, so there's no Sync to
Patch button, and the window's smallest size is 1024 × 640 (sLight's was 720 × 480), as the console's tabs need the room.

**Where slice 7d differs from sLight:** only FTDI interfaces through D2XX: Open DMX (the computer makes the break and
mark, at 250 kbaud 8N2) and the DMX USB Pro and its look-alikes (a framed "Send DMX Packet" message), each streamed
at 40 frames a second on a thread of its own. The uDMX (libusb) isn't ported: a show that names it sends as Open DMX.
`ftd2xx.dll` is loaded if it's installed, as the native extension did; without it the Output dropdown doesn't offer
USB, where sLight offered it and said in its device list that its extension wasn't built. The interface's kind is guessed from its name (PRO, DMXking or
ultraDMX means a Pro) unless it's chosen. The driver's stood in for in the tests, which check the calls and bytes
sent; not tried with an interface, as there's neither one nor the driver on this machine.

**Where slice 7c differs from sLight:** the visualizer pops out with a Pop Out button, and goes back with Dock to Main
or by closing its window, where sLight tore it off by dragging its tab away and docked it by dragging the window's
title onto the tab bar. Shadows are cast by the brightest eight lights, each from a 1024-pixel map compared with
four-texel filtering, where Godot gave every light its own; beams aren't cut by them, as sLight's cones weren't. Tried
in the app: a cube under a par shadowing part of its pool with Shadows on and not with them off; the visualizer
popped out into a window of its own, drawing there with its tab saying where it had gone, and docked again by
closing that window.

**Where slice 7b differs from sLight:** a recording's sound is whatever the computer's playing (WASAPI loopback on
the default output: DJ Edit Mode's song, or the DJ software's in DJ Perform Mode), where sLight mixed its own output
with the line-in; it's AAC rather than MP3, and the MP4 is written by Windows' Media Foundation (its COM objects called
through their vtables) rather than sLight's native encoder, so there's no PNG-sequence fallback. Frames are copied
back from the GPU a frame or two after they're drawn, and a frame missed while the screen's slow is written again so
the video keeps time; a silence (the loopback says nothing) is filled with silence for the same reason. Tried in the
app: the edges smooth with 4× MSAA; a screenshot saved as a PNG of the whole view; a four-second recording at 30 frames
a second with its video and sound tracks. The render folder is opened in Explorer after each, as sLight's was, but it
wasn't seen to open in that try.

**Where slice 7a differs from sLight:** none in what's read and written. A fixture's GDTF is read straight from the
archive rather than through a temporary file, and a GDTF reader for streams (`FixtureImport.FromGdtf`) came with it.
Checked: an MVR exported by Cuedeck, read by sLight's own `MvrIO` in headless Godot, gives back both fixtures, their
universes, channels, modes, places, headings and channel roles.

### 7.8 Phase 8 progress

| Slice | What | State |
|---|---|---|
| 8a | Bloom: the visualizer drawn in HDR, its bright parts blurred down a chain of smaller images and added back before the highlight roll-off, with sLight's glow settings (threshold 0.9, intensity 0.5, bloom 0.08) | ✅ |
| 8b | USB DMX through libusb (`libusb-1.0.dll`, P/Invoked when it's installed): the anyma uDMX (a control transfer per frame) and FTDI interfaces with no D2XX driver (their setup and break as vendor requests, the frame on the bulk endpoint) | ✅ |
| 8c | Import from sLight (`SlightImport`): sLight's own files in Godot's user:// folder (and its older "DMX Lighting Controller" one) copied into Cuedeck's app folder, which keeps them by the same names; offered at the first start and from Options | ✅ |

**Slice 8c** has no sLight counterpart: it's how someone moving from sLight brings what isn't in their show files.
Both apps keep the same files by the same names in the same formats, so it copies: custom fixture profiles and their
GDTF models, the online library's cache, its preset, the show it saved untitled (`dmx_show.json`, with its timelines);
merges the settings, remembered folders and window places a key at a time, with `user://` paths made Cuedeck's; and
remembers sLight's last show, which the first-start offer then opens. Nothing Cuedeck has is overwritten, and sLight's
folder is only read. Its autosave, session marker, renders, logs and caches stay behind. Tried on this machine's own
sLight folders into a scratch folder: both found, a remembered folder and the older folder's preset brought in.

**Where slice 8b differs from sLight:** the same devices, requests and bytes as sLight's libusb backend: the uDMX
(16C0:05DC, told from other devices on that shared ID by its name) gets one "set channel range" request a frame with
all 512 channels; a single-port FTDI chip (0403:6001 or 6015) is listed through libusb only when D2XX isn't
installed, set up with FTDI's vendor requests, and given its break by "set data" with the break bit before its frame
goes to bulk endpoint 2. One without a serial number is routed by where it's plugged in (`udmx:1:5`). Windows only,
so there's no detaching a kernel driver as sLight did on Linux. `libusb-1.0.dll` is loaded if it's installed (beside
the app or on the path), as the native extension did; the USB output is offered when either library is. The library's
stood in for in the tests (`LibusbDmxTests`), which check every request and the bytes sent; not tried with an
interface, as there's neither one nor libusb on this machine.

**Where slice 8a differs from sLight:** Godot 4.7's glow as sLight set it (threshold 0.9, intensity 0.5, bloom 0.08)
with Godot's defaults for the rest (an HDR scale of 2, a luminance cap of 12, levels 2, 3 and 4 weighted 0.8, 0.4
and 0.1, a Screen blend after tone mapping), but blurred by shrinking and enlarging filters rather than Godot's
Gaussian passes, and tone mapped by Cuedeck's highlight roll-off rather than ACES. The scene is drawn into a
half-float image (resolved from 4× MSAA) for it. With nothing bright in view it lifts the picture by about 2% (the
bloom setting), as Godot's does. Tried in the app: the passes line up with the beams, the right way up, with the glow
turned up for a look; the README and guide's visualizer shots taken again with it.

Phases 1 and 2 are the critical path; 3–5 can then go in any order, and 6 can start alongside them once phase 1's `DmxRender` exists.

## 8. Risks

- **UI volume.** About 16k lines of Godot UI get rewritten with no designer and fewer stock widgets. Build the missing widgets (§3) once and well before the panels that need them, and port panels in the order the User Guide walks through them.
- **3D scope.** Volumetric fog and GDTF models are where "comparable" can turn into months. The cone-beam approach and schematic bodies are the stop line for parity; anything past that is enhancement.
- **Show compatibility drift.** Godot's float-only JSON numbers and `to_dict()` quirks. The golden-file round-trip tests in §4 catch this; add one for every bug found.
- **Behaviour hidden in the scene tree.** Signals connected in `_ready`, `call_deferred` ordering and Timer-driven refreshes carry implicit timing. The GDScript tests are the best record of intended behaviour, so port the tests **before** the code they cover in each phase.
- **sLight keeps moving.** The source repo had commits this week. Freeze it at a known commit (`406366d`) for the port, and log later sLight changes for a catch-up pass.

## 9. Open decisions

1. **Product name and namespace:** this plan assumes "Cuedeck" (from the repo name).
2. **Windows-only:** the plan assumes yes, matching Cuepoint.
3. **Show format:** stay byte-compatible with sLight (assumed), or take the chance to version and clean it up behind an importer.
4. **Shared code with Cuepoint:** this plan copies the files. A shared library (say `Cuepoint.Framework`, made `public`) would avoid drift, but it means restructuring Cuepoint first.
