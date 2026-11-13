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

## Adapted

- `Midi/MidiMessage.cs`: Cuepoint's, with note-off and pitch bend told apart for the playback faders, and Program
  Change, the clock's tick and Start for triggers and the tempo clock.
- `Midi/WinMidi.cs`, `Midi/MidiDevices.cs`: Cuepoint's, unchanged but for the namespace (phase 3).

## New

- `Program.cs`, `UI/MainScreen.cs`, `AppPaths.cs`
- Everything under `Common/`, `Fixtures/`, `Dmx/` and `Show/` (phase 1), ported from sLight's GDScript
- `Audio/` (phase 3): sLight's FFT and sound engine, and WASAPI input through NAudio.Wasapi (the version Cuepoint
  uses, with NAudio 3's `WasapiRecorder` rather than Cuepoint's code)
- `UI/Layout.cs` (`Row`, `Flow`, `Region`, `ScrollingPage`, `Placeholder`, `Divider`, `FixedWidth`, `FieldGrid`), `UI/TabView.cs`,
  `UI/NumberBox.cs`, `UI/ColourPicker.cs`, `UI/Console/*` (among them `ProfileEditor` and `LibraryDialog`),
  `AppShell.cs` (phase 2)

## Ported across since

_(none yet)_
