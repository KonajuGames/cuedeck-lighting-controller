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

## Adapted

- `Midi/MidiMessage.cs`: Cuepoint's, with note-off and pitch bend told apart for the playback faders.

## New

- `Program.cs`, `UI/MainScreen.cs`, `AppPaths.cs`
- Everything under `Common/`, `Fixtures/`, `Dmx/` and `Show/` (phase 1), ported from sLight's GDScript

## Ported across since

_(none yet)_
