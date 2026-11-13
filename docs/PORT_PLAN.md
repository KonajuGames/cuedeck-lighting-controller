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
| 4 | **DJ Edit Mode.** Music browser (drives, VirtualDJ lists, Cuepoint library), song analysis, the structure bar, heatmap and timeline editor, timeline generator, timeline store and the position editor. Port `music_*`, `timeline_*`, `structure_detection`, `virtualdj_*`, `cuepoint_library` and `m4a_playback` tests. | Generate → edit → save a song's timelines, and the files load in sLight too. |
| 5 | **DJ Perform Mode.** OS2L server with mDNS advertising, the DJ performer mixing deck timelines, and the perform panel. Port `os2l_server` and `dj_perform` tests, and test against Cuepoint itself as the OS2L client. | Cuepoint and VirtualDJ drive the lights live. |
| 6 | **Visualizer.** §5 steps 1–3, then 4. Port the `visualizer_undo` and `fixture_view` tests. | The rig in `docs/img/09-visualizer.png` looks comparable. |
| 7 | **Native extras and polish.** USB DMX (FTDI/libusb P/Invoke), MP4 recording, visualizer pop-out window and shadows, MVR import/export, screenshot capture tool, user guide and README rewritten with new screenshots. | Feature parity checklist (from sLight's README) all ticked. |

### 7.1 Phase 1 as built

**Where it is:** `Cuedeck/Common` (Godot-style rounding, colours with Godot's colour names, lenient JSON), `Cuedeck/Fixtures` (profiles, built-ins, `DmxRender`), `Cuedeck/Dmx` (universes, Art-Net and sACN packets, `DmxOutput` compositing and crossfade), `Cuedeck/Midi/MidiMessage.cs`, and `Cuedeck/Show`. `LightingShow` wires together everything sLight's shell wired together (patch, groups, cue list, chases, effects, reactors, palettes, positions, faders, live flash). `ShowFile` reads and writes sLight's format, and `ShowSession` covers the current file, unsaved changes, the remembered show and autosave recovery.

**How it was checked:** 73 xUnit tests, ported from sLight's `dmx_render`, `virtual_dimmer`, `tempo_clock`, `live_flash`, `palettes`, `cue_timing`, `faders`, `position`, `show_file`, `unsaved_changes`, `autosave` and `fixture_profile` tests, plus packet-layout tests. The phase's "done when" is met with a golden scenario instead of UDP capture. `Cuedeck.Tests/Golden/scenario.slight` is a two-universe show built by Cuedeck, and `tools/slight_frames.gd` plays a 320-frame script on it in sLight 1.0 (`406366d`, Godot 4.7) and records the output. `SlightOutputTests` plays the same script in Cuedeck and requires every frame to match byte for byte. It does, across cues, delays, follows, palettes, chases, effects, faders, virtual dimmers, a live flash, the grand master and a blackout. sLight loading a show written by Cuedeck is part of that check too. Packets are compared by layout rather than captured, since the frame is all that varies.

**Left for later phases, on purpose:**
- Fixture library, GDTF/QXF import and MVR (`fixture_library`, `fixture_import`, `mvr_io`, and those parts of `fixture_profile_test`) go to phase 2, with the Patch tab that uses them.
- MIDI/OSC triggers and the visualizer's stage are carried through show files unchanged (`LightingShow.Carried`), so nothing is lost. They're ported in phases 3 and 6.
- Live audio: reactors run against `ISoundLevels`, which is silent until phase 3.
- USB DMX: `DmxOutput.SendUsb`/`ReleaseUsb` hooks only (phase 7).
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
