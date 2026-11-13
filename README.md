# Cuedeck

Cuedeck is a DMX lighting controller for Windows. It's the sLight lighting controller (a Godot 4 project) being
ported to the C# and Vulkan framework that the Cuepoint DJ app is built on. The plan, and how far it's got, are in
[docs/PORT_PLAN.md](docs/PORT_PLAN.md).

**Status:** phase 2, under way. The lighting engine is ported: the patch, Art-Net and sACN output, cues, chases,
effects, palettes, positions, faders, live flashes, the tempo clock and sLight's show files, and its output matches
sLight's frame for frame. On screen so far are the console's top bar (grand master, run mode, tap tempo, Sending,
Blackout All, universes, and New, Save and Load Show), and all of the console's tabs but Sound and the 3D
Visualizer: Cues (Space is GO), Chases, Effects, Groups, Patch (with the bundled fixture library), DMX Output,
Faders and Palettes. Profiles can be made and edited, imported from GDTF and Open Fixture Library files, or chosen
from the fixture library: the bundled one, the current Open Fixture Library online, or GDTF-Share with an account
(its login is never saved). The bundled library is the Open Fixture Library's (see
[its attribution](Cuedeck/Fixtures/Library/ATTRIBUTION.md)).

To open a show straight away, pass it on the command line: `dotnet run --project Cuedeck -- "My show.slight"`.

It's written in C# on .NET 10, and draws its own interface with Vulkan through
[BRUTAL](https://www.nuget.org/packages?q=Ahwoo.BRUTAL) (GLFW, Vulkan, FreeType).

## Building and running

You need the .NET 10 SDK and a GPU with Vulkan 1.3 drivers.

```bash
dotnet run --project Cuedeck
```

Debug builds turn on Vulkan's validation layers when the Vulkan SDK is installed.

## Tests

```bash
dotnet test
```

## Layout

- `Cuedeck/`: the app. `Rendering/`, `Input/` and most of `UI/` come from Cuepoint (see [FRAMEWORK.md](FRAMEWORK.md));
  `Fixtures/`, `Dmx/` and `Show/` are the lighting engine, ported from sLight.
- `Cuedeck.Tests/`: xUnit tests, which run without a window or a GPU. `Golden/` holds a show and sLight's output for
  it, frame by frame, which Cuedeck's must match.
- `tools/slight_frames.gd`: records that output from sLight.
- `docs/`: the port plan.

## License

MIT; see [LICENSE](LICENSE).
