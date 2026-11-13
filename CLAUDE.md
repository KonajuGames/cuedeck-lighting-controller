# Cuedeck

A port of sLight (Godot 4 DMX controller, `F:\Git\slight-dmx-controller`, frozen at `406366d`) onto Cuepoint's
C#/.NET 10 + Vulkan UI framework (`F:\Git\cuepoint-dj-controller`, copied from `6f1ee3e`). The plan and phase list
are in `docs/PORT_PLAN.md`; what was copied from Cuepoint and changed is in `FRAMEWORK.md`. Keep both up to date.

## Commands

- Build: `dotnet build Cuedeck.slnx`
- Test: `dotnet test Cuedeck.slnx`
- Run: `dotnet run --project Cuedeck`

## Conventions

- Follow Cuepoint's style: file-scoped namespaces, `sealed`/`internal`-by-default classes, `float2`/`float4` from
  `Brutal.Numerics`, colours as sRGB hex via `Colors.Hex` and named in `UI/Theme.cs`, and doc comments that say
  what something is for in plain words.
- UI is retained-mode: `Element.Measure()` then `Arrange()` every frame, then `Draw(Batch)`. Interactive elements
  override `IsInteractive` and the pointer/key methods; `Root` routes input.
- Port sLight's GDScript tests (in `tests/*.gd`) to xUnit **before** the code they cover, and treat sLight's
  behaviour and README as the spec.
- Show files, profiles and timelines must stay readable by sLight (JSON; Godot writes every number as a float).
- App data goes under `AppPaths.Folder` (`%LOCALAPPDATA%\Cuedeck`), never next to the executable.
- Tests must run without a window, GPU, sound card or network.
