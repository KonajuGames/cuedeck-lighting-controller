# Where the framework came from

Cuedeck's UI, rendering and window code was copied from [Cuepoint](../cuepoint-dj-controller) at commit
`6f1ee3e` ("Add Auto-Mix…"), with the namespace `Brutality` renamed to `Cuedeck`. The two apps change
independently from here on; when a fix lands in one that the other needs, port it across by hand and note it below.

The port's source, sLight, is frozen at commit `406366d` of `slight-dmx-controller`.

## Copied as they were (apart from the namespace)

- `Rendering/*`: Vulkan context, swapchain, renderer, shader loader
- `Input/*`: GLFW input to `Root`, keys, modifiers, mouse buttons
- `Shaders/ui.vert`, `Shaders/ui.frag`
- `UI/`: `Elements`, `Root`, `Batch`, `FontAtlas`, `FreeType`, `Images`, `Png`, `OverlayRenderer`, `ScrollPanel`,
  `ListView`, `TreeView`, `TextBox`, `Slider`, `Knob`, `Checkbox`, `Dropdown`, `ContextMenu`, `ConfirmDialog`,
  `NamePrompt`, `LevelMeter`
- `WindowChrome.cs`, `WindowsShell.cs` (crash box retitled), `ErrorLog.cs`
- `Assets/*.png` icons and `Assets/draw_assets.py`

## Changed

- `UI/Theme.cs`: kept the panel, text and accent colours; dropped the deck, hotcue and Camelot colours; `Hsv` made
  public.
- `UI/TitleBar.cs`: kept `IconButton` and `Spacer`; the bar itself is new: logo, name and show, an empty `Centre`
  for the console's controls, the clock, options and the window's buttons.
- `Assets/draw_assets.py`: no longer draws a logo. The logo PNGs come from sLight's icon via `Assets/make_logo.py`.

- `UI/Elements.cs`: `Element.FocusLost()`; `Button.Lit` (a toggle that's on, the tab showing); `Label.Wrap`.
- `UI/Root.cs`: calls `FocusLost()` on the element losing focus; a key the focused element doesn't use goes to the
  elements it's in, nearest first (so a dialog closes on Escape whichever of its controls has focus).
- `UI/TextBox.cs`: no longer sealed (`NumberBox` builds on it); `Secret` shows dots for what's typed and won't copy or
  cut it (GDTF-Share's password).
- `UI/Theme.cs`: the Sound tab's meter colours and the spectrogram's heat scale.
- `UI/Png.cs`: `Encode` (RGBA, unfiltered) and a bilinear `Resize`, for gobo pictures imported from GDTF files.
- `UI/ConfirmDialog.cs`: an optional second way to go ahead (Save / Don't Save / Cancel), shown in blue rather
  than red.
- `UI/FontAtlas.cs`: arrows and the minus sign added to the characters baked.
- `UI/Batch.cs`: `Colors.FromSrgb`.
- `UI/Batch.cs` (slice 4f): triangles with a colour per corner, lines, polylines, dashed lines, convex polygons,
  circles and arcs, all cut to the clip rectangle (Sutherland-Hodgman, their colours and texture coordinates with
  them), and `AddTextUp` for text turned a quarter turn (the timeline's name plates).
- `UI/Root.cs` (slice 4f): `PointerHooks` (`IPointerHook`), which see the mouse before the elements do (the
  timeline's middle-button pan); `Element.PointerUp` when a press ends; right-clicks pass the keys held
  (`SecondaryClick(point, modifiers)`); the up-down, four-way and grabbing cursors (`Cursor.ResizeVertical`, `Move`,
  `Grab`), in `Input/GlfwUiInput.cs` too; and `Key.D`.
- `UI/Theme.cs` (slice 4f): the song timeline's colours.
- `UI/Images.cs` (slice 4g): `Timeline`, DJ Edit Mode's "has lighting timelines" mark, drawn when the app starts
  rather than read from a PNG.
- `UI/ListView.cs` (slice 4h): `SelectItems`, to select several rows from code.
- `UI/FontAtlas.cs` (slice 4h): the up and down arrows baked too.
- `UI/MainScreen.cs` and `UI/Console/TopBar.cs` (slice 4g): the View dropdown and DJ Edit Mode in place of the
  console.
- `UI/MainScreen.cs` and `UI/Console/TopBar.cs` (slice 5c): DJ Perform Mode in the View dropdown too.
- `Input/Key.cs` and `Input/GlfwUiInput.cs` (slice 5c): `Key.B`, `Key.S` and `Key.W`, for the live flashes.
- `UI/Console/OptionsDialog.cs` (slice 5c): `Settings`, the settings it edits.
- `UI/Batch.cs` and `UI/OverlayRenderer.cs` (slice 6b): `Batch.AddTexture`, a quad from another texture than the font
  atlas (the 3D view's picture), drawn as a run of its own with that texture's descriptor set
  (`OverlayRenderer.SetTexture`).
- `Rendering/Renderer.cs` (slice 6b): the 3D pass (`Scene3D`, `IScene3DSource`) recorded before the UI each frame.
- `UI/Root.cs` (slice 6b): `Mouse`, where the pointer is.
- `UI/NumberBox.cs` (slice 6b): `Submitted`, raised on Enter.
- `Rendering/Barriers.cs` (slice 6d): `TransitionImage` takes a range of array layers too.

## Adapted

- `Midi/MidiMessage.cs`: Cuepoint's, with note-off and pitch bend told apart for the playback faders, and Program
  Change, the clock's tick and Start for triggers and the tempo clock.
- `Midi/WinMidi.cs`: Cuepoint's, unchanged but for the namespace (phase 3).
- `Midi/MidiDevices.cs`: Cuepoint's, with the choice not to open each device's output (trigger feedback opens the one
  chosen instead).

## New

- `Program.cs`, `UI/MainScreen.cs`, `AppPaths.cs`
- Everything under `Common/`, `Fixtures/`, `Dmx/` and `Show/` (phase 1), ported from sLight's GDScript
- `Audio/` (phase 3): sLight's FFT and sound engine, and WASAPI input through NAudio.Wasapi (the version Cuepoint
  uses, with NAudio 3's `WasapiRecorder` rather than Cuepoint's code)
- `UI/Layout.cs` (`Row`, `Flow`, `Region`, `ScrollingPage`, `Placeholder`, `Divider`, `FixedWidth`, `FieldGrid`), `UI/TabView.cs`,
  `UI/NumberBox.cs`, `UI/ColourPicker.cs`, `UI/Console/*` (among them `ProfileEditor` and `LibraryDialog`),
  `AppShell.cs` (phase 2)

## Ported across since

- `Audio/AudioDecoder.cs`, `Audio/MediaFoundationDecoder.cs`, `Audio/PcmAudio.cs` (slice 4c): Cuepoint's
  `Brutality/Audio` decoders (SDL_mixer, and Media Foundation for M4A/AAC), unchanged but for the namespace, to decode
  songs for analysis.
