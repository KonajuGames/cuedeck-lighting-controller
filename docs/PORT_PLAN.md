# Porting sLight to Cuepoint's framework: the plan

**Source:** `F:\Git\slight-dmx-controller`. A Godot 4.7 project with about 41k lines of GDScript (25k in the app, 16k in tests), an optional C++ GDExtension (`addons/native`) and a 300-maker fixture library in JSON.
**Framework:** `F:\Git\cuepoint-dj-controller`. C# on .NET 10, with its own retained-mode UI (`Brutality/UI`) drawn through Vulkan via the BRUTAL packages (GLFW, Vulkan, VMA, FreeType, ShaderC, SDL3), plus xUnit tests.
**Target:** this repository, `cuedeck-lighting-controller`.

## 1. What carries over and what doesn't

| sLight piece | Lines | What happens in the port |
|---|---|---|
| DMX core: `artnet_universe`, `artnet_sender`, `sacn`, `dmx_render`, `cue`, `chase`, `wave_effect`, `effects_engine`, `live_flash`, `palette_library`, `position_library` | ~2.2k | **Straight translation** to plain C# classes. Godot is only used here for `PacketPeerUDP` and `PackedByteArray`, which become `UdpClient` and `byte[]`. |
| Show model and persistence: `timeline_store`, `timeline_track`, `timeline_generator`, `autosave`, `app_settings`, `fixture_profile`, `fixture_library`, `fixture_import`, `mvr_io` | ~6.5k | **Translate.** Keep the JSON formats byte-compatible so existing `.slight` shows, profiles and timelines load (see §4). |
| Tempo, sound and triggers: `tempo_clock`, `sound_engine`, `sound_reactor`, `triggers_engine`, `trigger`, `osc`, `feedback_out`, `fft`, `spectrogram` | ~1.6k | **Translate the logic and replace the engine I/O.** Godot's `AudioEffectCapture` becomes a WASAPI capture (NAudio.Wasapi is already in Cuepoint) and `InputEventMIDI` becomes Cuepoint's `WinMidi`. |
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
| 2 | 🔶 **Console UI** (in progress; see §7.2). Shell (top bar, grand master, run mode, Sending, universes, save/load), Patch/universe tabs with the fixture library dialog, then Cues, Chases, Effects, Groups, Faders/fader bank, Palettes and DMX Output. New widgets from §3 as each tab needs them. | Every console tab works for the sLight User Guide walkthrough. |
| 3 | **Sound and triggers.** Live capture, sound reactors and the Sound tab with spectrogram. MIDI/OSC triggers with Learn and feedback (WinMidi out). Options dialog (VirtualDJ or Cuepoint). | Beat-reactive chases run from line-in; a controller's LEDs follow. |
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

### 7.2 Phase 2 progress

Phase 2 is done in slices, each leaving the app working.

| Slice | What | State |
|---|---|---|
| 2a | The show running in the app (30 Hz tick, Sending, autosave every minute, crash recovery, reopening the last show or one given on the command line, unsaved-changes prompts on New, Load and close); new widgets (`TabView`, `NumberBox`, `Row`, `Flow`, `Region`/`ScrollingPage`, lit buttons, wrapping labels); the top bar (grand master, run mode, Tap and tempo, Sending, Blackout All, Add and Remove Universe, New, Save, Save As and Load with Windows' own dialogs, and their shortcuts); the console's layout; the Patch tab (connection settings, Blackout Universe and Universe Full, adding fixtures from the built-in and custom profiles, fixture rows with category, category group, Leave out, Home, Remove, and controls: virtual dimmer, colour swatches, pan/tilt pad, 16-bit sliders, slot pickers, sliders) | ✅ |
| 2b | Cues tab (GO, Back, Halt, the progress bar, the list, editing a cue, Record and Update, tracking) and Space for GO | next |
| 2c | Chases, Effects and Groups tabs | |
| 2d | Faders and Palettes tabs, DMX Output tab | |
| 2e | Fixture library dialog, profile import (GDTF/QXF), New/Edit/Delete profile; a colour picker for the swatches; patch Flash; Save/Load Preset | |

What runs on the UI thread for now: the show ticks in the main loop, so its output pauses while one of Windows' file dialogs is open (Art-Net and sACN receivers hold the last frame). Moving the tick to its own thread (§6) comes with the locking that needs.

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
