# Cuedeck

A port of sLight (Godot 4 DMX controller, `F:\Git\slight-dmx-controller`, frozen at `406366d`) onto Cuepoint's
C#/.NET 10 + Vulkan UI framework (`F:\Git\cuepoint-dj-controller`, copied from `6f1ee3e`). The plan and phase list
are in `docs/PORT_PLAN.md`; what was copied from Cuepoint and changed is in `FRAMEWORK.md`. Keep both up to date.

## Commands

- Build: `dotnet build Cuedeck.slnx`
- Test: `dotnet test Cuedeck.slnx`
- Run: `dotnet run --project Cuedeck`
- Screenshots for the README and user guide: `dotnet run --project Cuedeck -- --capture docs/img [name ...]`, then move
  `docs/img/screenshot.png` to `docs/`. It runs on a scratch app folder with a demo rig and made-up songs.

## Conventions

- Follow Cuepoint's style: file-scoped namespaces, `sealed`/`internal`-by-default classes, `float2`/`float4` from
  `Brutal.Numerics`, colours as sRGB hex via `Colors.Hex` and named in `UI/Theme.cs`, and doc comments that say
  what something is for in plain words.
- UI is retained-mode: `Element.Measure()` then `Arrange()` every frame, then `Draw(Batch)`. Interactive elements
  override `IsInteractive` and the pointer/key methods; `Root` routes input.
- Port sLight's GDScript tests (in `tests/*.gd`) to xUnit **before** the code they cover, and treat sLight's
  behaviour and README as the spec.
- Cuedeck must read sLight's show files, profiles and timelines (JSON; Godot writes every number as a float).
  Compatibility is one-way: files Cuedeck writes needn't open in sLight, so new keys and values are fine, but
  existing saved numbers and keys must keep their meaning (add enum values on the end, never reorder).
- App data goes under `AppPaths.Folder` (`%LOCALAPPDATA%\Cuedeck`), never next to the executable.
- Tests must run without a window, GPU, sound card or network.
- Port GDScript arithmetic exactly: `Gd.Round`/`Gd.RoundToInt` (halves away from zero, unlike C#'s default) for
  `round()`, and a plain cast for `int()`. Read show JSON leniently with the `Json` helpers (`Int`, `Num`, `Str`,
  `Bool`), as Godot reads every number as a float.
- Screens fill the room they're given by being a `Region` (set `Available` before `Measure`); a tab's page is
  usually a `ScrollingPage`. Controls that mirror the show (sliders, labels) are synced from it in an `Update()`
  called each frame, not pushed to by the engine. The console's code is in `UI/Console`; `AppShell` holds the
  file commands and prompts.
- UI tests drive the real screen (`Cuedeck.Tests/UI/ConsoleTests`): lay it out, then `Root.MouseMove` and
  `MouseButton` on the element found by its text or tooltip.
- `LightingShow` is the engine with no UI; screens read and drive it. Keep logic sLight kept in its panels (cue
  playback, faders, patch homing) in the engine, where tests can reach it.

## Golden scenario (sLight parity)

`Cuedeck.Tests/Golden/SlightOutputTests` checks Cuedeck's DMX against sLight's, frame by frame. After changing
`GoldenScenario.Build()` or its script, regenerate both halves:

1. `CUEDECK_WRITE_GOLDEN=1 dotnet test Cuedeck.slnx --filter The_scenario_show` writes `scenario.slight`.
2. Record sLight's frames (Godot is at `C:\Godot_v4.7-stable_mono_win64`; keep the script list in
   `tools/slight_frames.gd` the same as `GoldenScenario.Script`):
   `Godot_v4.7-stable_mono_win64_console.exe --headless --path F:\Git\slight-dmx-controller --script F:\git\cuedeck-lighting-controller\tools\slight_frames.gd -- <repo>\Cuedeck.Tests\Golden\scenario.slight <repo>\Cuedeck.Tests\Golden\scenario.frames.json`

A fresh sLight checkout needs `--headless --path <sLight> --import` once first. That rewrites sLight's tracked
`*.import` files: restore them with `git checkout` so its tree stays clean.

`Cuedeck.Tests/Golden/SlightGeneratorTests` checks the timeline generator's shows the same way. After changing its
cases, keep `tools/slight_generator.gd` in step and rerecord `generator.json` with it:
`Godot_v4.7-stable_mono_win64_console.exe --headless --path F:\Git\slight-dmx-controller --script F:\git\cuedeck-lighting-controller\tools\slight_generator.gd -- <repo>\Cuedeck.Tests\Golden\generator.json`
