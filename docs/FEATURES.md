# sLight's features in Cuedeck

Every feature sLight's README describes (at `406366d`), ticked off against Cuedeck. ✅ is there and works as sLight's
did; ➕ is there but works differently, as noted; ❌ isn't ported. Where a slice changed something on purpose, the
[port plan](PORT_PLAN.md) says more, under that slice's "Where it differs from sLight".

## Universes and output

- ✅ One to eight universes, each with its own patch and 512 channels; Add Universe (Ctrl+Shift+N), Remove Universe
  (Ctrl+Shift+R) after asking, new ones numbered to the first free Art-Net number
- ✅ Art-Net: IP, port and universe number per universe, several universes on one number at different IPs
- ✅ sACN (E1.31): multicast or unicast, priority, a source identity kept in the show
- ➕ USB DMX: Enttec DMX USB Pro and look-alikes, and Open DMX, through FTDI's D2XX driver, P/Invoked rather than
  through a native extension; the USB output is offered only when D2XX or libusb is installed (sLight showed it greyed)
- ✅ USB DMX through libusb (`libusb-1.0.dll`): the anyma uDMX, and single-port FTDI interfaces where D2XX isn't
  installed, each needing a libusb or WinUSB driver bound to it
- ✅ A universe whose USB interface can't be opened, or a USB show opened without the driver, falls back to Art-Net
- ✅ Grand Master (non-destructive, on the way out), Sending (30 frames a second), Blackout All
- ✅ Output matches sLight's frame for frame (`Golden/SlightOutputTests`)

## The show

- ✅ Save Show, Save Show As, Load Show, New Show (keeping Universe 1's output), with their shortcuts
- ✅ One `.slight` file: connections, patches with embedded profiles, live levels and virtual dimmers, cues, chases,
  effects, groups, palettes, faders, positions, reactors, triggers, run mode, the visualizer's stage; read and written
  as sLight does, so either app opens the other's shows
- ✅ Format versions: a newer file or a non-show refused; older shows, uid-less fixtures and a bare patch array read
- ✅ The songs' timelines in a folder beside the show, copied by Save Show As, switched by Load Show
- ✅ The last show reopened at start, with the reason if it can't be
- ✅ Autosave every minute with unsaved changes, a session marker, and Restore or Discard after a crash
- ✅ Save Preset and Load Preset (the live levels), legacy flat presets, presets wider than the show
- ✅ Each file dialog reopens in its own last folder (`recent_dirs.json`)

## Playback

- ✅ Cue Mode and Sound Reactive run modes (an old Auto Show mode opens as Cue Mode)
- ✅ Cue list: Record Cue, GO (Space), Back, Halt, split fade up and down, delay, follow with a wait, the progress bar,
  inline editing, Update, Duplicate, Delete, Load to Patch
- ✅ Tracking and block cues, ripples down the list, `[T]` and `[B]`, older shows' cues as blocks
- ✅ Chases: steps recorded, updated, loaded and deleted; tempo, crossfade, forward, backward and bounce; several at
  once; beat sync to the tempo clock or the audio
- ✅ Effects: roles, universe or group targets, Absolute and Pickup, five waveforms, rate, size, fan, phase, motor
  roles capped at 60 BPM, Follow tempo in beats, Rebuild targets
- ➕ Groups: named fixture sets by uid, kept through renames and universe removal; the checklist follows the patch by
  itself, so there's no Sync to Patch button
- ✅ Palettes: colour, position and beam, per fixture by uid, recorded, applied, updated from live, renamed, deleted;
  cues referring to them (◆), baked into cues when deleted
- ✅ Faders: cue submasters, chases, group masters, effects; MIDI Learn of a CC, pitch bend or note, with 2% end zones;
  Blackout All pulling them down
- ✅ Live flashes: Blackout, Strobe, White, Colour, held or latched, from the buttons, B / S / W / C, or a trigger
- ✅ Tempo clock: Tap (and T), MIDI clock, VirtualDJ's beats, the ✕ to stop it, the read-out flashing on the beat
- ✅ Sound tab: input and Rescan, the band meters, spectrogram, gain, beat sensitivity, response; reactors in Follow
  and Pulse, with the four starters

## Triggers and feedback

- ✅ MIDI (note, CC, program change, pitch bend, channel or any) and OSC bindings, Learn, every action sLight had,
  held flashes, the last message shown, Rescan
- ✅ Feedback to pads: following the action, the beat, Sending, anything running, the run mode; OSC feedback
- ➕ MIDI feedback goes straight to a Windows MIDI output, with no native extension needed; with none chosen it still
  goes to sLight's UDP bridge script, which isn't in this repository
- ✅ MIDI input heard whatever has focus

## Fixtures

- ✅ Built-in profiles, modes, roles, defaults, limits, 16-bit channels, named ranges with colours and pictures,
  physical blocks, GDTF geometry
- ✅ Fixture controls: colour pickers per head, the pan/tilt pad (with the wheel's nudge), 16-bit sliders, slot
  dropdowns with icons and trims, sliders, virtual dimmers, Flash, Home, Remove
- ✅ The library: about 640 bundled fixtures with search and filters, the Open Fixture Library online with a cache,
  GDTF-Share with a session-only login, Add to Patch
- ✅ New, Edit (passed on to patched fixtures) and Delete of profiles, kept in the app's folder
- ✅ GDTF and Open Fixture Library import, with gobo pictures and warnings
- ➕ The profile editor's head positions are typed on one line, and channels move with ▲ and ▼ rather than by
  dragging (Cuedeck's text boxes are single-line)

## 3D Visualizer

- ✅ The rig lit live by the console's output, with Sending on or off; Hide UI
- ✅ Camera: orbit, pan, zoom, five preset views and your own, FOV, Ortho
- ✅ Placement: select, drag, type; Auto-arrange; trusses; glTF set pieces; undo and redo
- ✅ Haze, rooms, work light, shadows (the brightest eight lights cast them, where Godot let every light)
- ✅ Bloom on bright beams, lenses and pools: Godot's glow with sLight's settings, drawn in HDR
- ✅ Multi-head fixtures with their own aims; colour, intensity, pan/tilt, zoom, strobe, gobos projected and spinning;
  GDTF models with their axes, or a body by category
- ➕ Screenshot (PNG) and Record (MP4, H.264); the recording's sound is whatever the computer plays, as AAC, where
  sLight mixed its own output and the line-in as MP3; there's no PNG-sequence fallback, as Windows always has the
  encoder
- ➕ Pop out: a Pop Out button and Dock to Main (or closing the window), where sLight tore the tab off by dragging it;
  where the window was is kept in `ui_state.json`, as sLight's code did (its README said the show)
- ✅ MVR import and export, checked against sLight's own reader

## DJ Edit Mode

- ✅ The folder tree, VirtualDJ's and Cuepoint's lists, the track list with its columns and sorting, missing files,
  the timelines icons (blue, and orange for temporary)
- ✅ Regenerate Lighting Timelines for a batch of songs, with progress and an undo per song
- ✅ Song analysis (tempo, beats, sections, key) on the DJ software's beat grid; M4A and AAC through Windows
- ✅ The structure bar: locked by default, borders dragged and snapped, sections relabelled, split and deleted
- ✅ The waveform, overview, bar ruler, beat grid, zoom, scroll and pan; play, pause and back to start; the playhead
- ✅ Master, fixture and head timelines with dimmer, colour, strobe, position and movement rows, inheriting as sLight's
- ✅ Selection (Ctrl, Shift, edges, moving with a ghost), Delete, cut, copy, paste, Paste Here, undo and redo, the
  right-click menus
- ✅ Mute, solo and collapse, saved per song
- ✅ Generate, for the whole song or a selection, and Shift for a fresh analysis; categories, groups and Leave out;
  shows matching sLight's (`Golden/SlightGeneratorTests`)
- ✅ The open song live on the rig while it plays, held at the playhead while paused
- ✅ Named positions: the list, the editor with live aiming, Ctrl+C and Ctrl+V, Snap Together, Spread, Reset
- ✅ Timelines saved on leaving a song and with the show, keyed by fingerprint

## DJ Perform Mode

- ✅ OS2L server on port 9960, advertised over mDNS as the app (on Windows; sLight also had macOS and Linux)
- ✅ Up to four decks, the skin's deck count, each deck's song, play state, volume fader and position
- ✅ Timelines loaded in the background and mixed: HTP for levels, pan and tilt averaged; strobe, positions and
  movement driven as sLight's
- ✅ Temporary shows for songs without any: a stand-in on the beat, then a generated show, saved and flagged
- ✅ VirtualDJ's beats driving the tempo clock; leaving the view stopping it all

## The app

- ➕ The window can be resized down to 1024 × 640 (sLight's went to 720 × 480), as the console's tabs need the room
- ✅ Scroll bars wherever a tab or list is too tall for the window
- ➕ App data lives in `%LOCALAPPDATA%\Cuedeck` rather than Godot's `user://`; sLight's is imported from there (custom
  profiles and models, settings, remembered folders, preset, untitled and last show), offered at the first start and
  in Options
- ➕ Windows only: the audio, MIDI, USB, mDNS and recording all use Windows' own APIs
