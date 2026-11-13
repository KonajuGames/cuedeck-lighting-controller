# Cuedeck

Cuedeck is a DMX lighting controller for Windows. It's the sLight lighting controller (a Godot 4 project) being
ported to the C# and Vulkan framework that the Cuepoint DJ app is built on. The plan, and how far it's got, are in
[docs/PORT_PLAN.md](docs/PORT_PLAN.md).

**Status:** phase 0. The app opens a window with its title bar; none of the lighting features have been ported
yet.

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

- `Cuedeck/`: the app. `Rendering/`, `Input/` and most of `UI/` come from Cuepoint (see [FRAMEWORK.md](FRAMEWORK.md)).
- `Cuedeck.Tests/`: xUnit tests, which run without a window or a GPU.
- `docs/`: the port plan.

## License

MIT; see [LICENSE](LICENSE).
