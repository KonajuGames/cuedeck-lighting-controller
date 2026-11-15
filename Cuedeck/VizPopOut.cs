using Brutal.GlfwApi;
using Brutal.Numerics;
using Cuedeck.Input;
using Cuedeck.Rendering;
using Cuedeck.UI;
using Cuedeck.UI.Console;
using Cuedeck.UI.Visualizer;

namespace Cuedeck;

/// <summary>
/// The 3D Visualizer popped out into an OS window of its own (to put on another screen, say), drawn by a renderer of
/// its own on the same GPU, with its own root, menu and name prompt; its tab says where it's gone. Closing the window
/// or pressing Dock to Main puts it back. Where the window was, and whether the visualizer's controls show, are kept
/// between runs (<see cref="VizWindowState"/>). sLight's <c>_pop_out_viz</c> and <c>_dock_viz</c>.
/// </summary>
sealed class VizPopOut : IDisposable
{
    readonly GpuContext _gpu;
    readonly FontAtlas _atlas;
    readonly Fonts _fonts;
    readonly VisualizerView _view;
    readonly ConsoleView _console;
    readonly Renderer _mainRenderer;
    readonly Root _mainUi;
    readonly ContextMenu _mainMenu;
    readonly NamePrompt _mainPrompt;
    readonly Func<bool> _shownDocked;
    readonly Action<string> _log;
    readonly string _statePath;

    GlfwWindow? _window;
    Renderer? _renderer;
    Root? _ui;
    GlfwUiInput? _input;
    ContextMenu? _menu;
    NamePrompt? _prompt;

    public VizPopOut(
        GpuContext gpu,
        FontAtlas atlas,
        Fonts fonts,
        ConsoleView console,
        Renderer mainRenderer,
        Root mainUi,
        ContextMenu mainMenu,
        NamePrompt mainPrompt,
        Action<string> log,
        string statePath
    )
    {
        (_gpu, _atlas, _fonts, _console, _mainRenderer, _mainUi, _mainMenu, _mainPrompt, _log, _statePath) = (
            gpu,
            atlas,
            fonts,
            console,
            mainRenderer,
            mainUi,
            mainMenu,
            mainPrompt,
            log,
            statePath
        );
        _view = console.Visualizer;
        _shownDocked = _view.IsShown;
        _view.PopOutRequested += () => PopOut(null);
        _view.DockRequested += Dock;
    }

    /// <summary>
    /// Where a window opened for the first time goes: towards the top left, clear of the screen's edge.
    /// </summary>
    static readonly int2 DefaultPosition = new(160, 120);

    public bool IsOpen => _window is not null;

    /// <summary>
    /// Puts the visualizer where it was last time.
    /// </summary>
    public void Restore()
    {
        var state = VizWindowState.Load(_statePath);
        _view.UiVisible = state.Ui;
        if (state.Floating)
            PopOut(state.Rect);
    }

    /// <summary>
    /// Opens the window (at <paramref name="rect"/>, or near the main window) and moves the visualizer into it.
    /// </summary>
    public void PopOut((int X, int Y, int Width, int Height)? rect)
    {
        if (_window is not null)
            return;
        Glfw.WindowHint(GlfwWindowHint.Visible, 1);
        Glfw.WindowHint(GlfwWindowHint.Maximized, 0);
        var size = rect is { Width: > 200, Height: > 200 } r ? new int2(r.Width, r.Height) : new int2(1000, 620);
        var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuedeck — 3D Visualizer", Size = size });
        window.SetSizeLimits(new int2(480, 320), new int2(-1, -1));
        window.Position = rect is { Width: > 200 } at ? new int2(at.X, at.Y) : DefaultPosition;
        if (_gpu.CreateSurface(window) is not { } surface)
        {
            window.Dispose();
            _log("The 3D Visualizer can't have a window of its own on this graphics card.");
            return;
        }
        _window = window;
        _renderer = new Renderer(_gpu, window, _atlas, surface);
        _ui = new Root { TooltipFont = _fonts.Body };
        _menu = new ContextMenu(_fonts.Body);
        _prompt = new NamePrompt(_ui, _fonts.Body, _fonts.Bold, _fonts.Heading);

        _console.DetachVisualizer(_fonts.Body);
        _ui.Add(_view);
        _ui.Add(_prompt);
        _ui.Add(_menu);
        _view.Root = _ui;
        _view.UseDialogs(_menu, _prompt);
        _view.Floating = true;
        _view.IsShown = () => _window is { IsIconified: false };
        _mainUi.PointerHooks.Remove(_view);
        _ui.PointerHooks.Add(_view);
        _mainRenderer.Scene = null;
        _renderer.Scene = _view;

        _input = new GlfwUiInput(window, _ui);
        // Its Ctrl+Z / Ctrl+Y.
        _input.UnhandledKey += (key, action, mods) =>
        {
            if (action == GlfwKeyAction.Press)
                _view.KeyDown(GlfwUiInput.ToUiKey(key), (Modifiers)((int)mods & 0xF), _ui.Mouse);
        };
        _log("3D Visualizer popped out — close its window, or press Dock to Main, to bring it back.");
        SaveState();
    }

    /// <summary>
    /// Puts the visualizer back in its tab and closes the window.
    /// </summary>
    public void Dock()
    {
        if (_window is null)
            return;
        SaveState(floating: false);
        _ui!.Elements.Remove(_view);
        _ui.PointerHooks.Remove(_view);
        _renderer!.Scene = null;
        _renderer.Dispose();
        _input!.Dispose();
        _window.Dispose();
        (_window, _renderer, _input, _ui, _menu, _prompt) = (null, null, null, null, null, null);

        _view.Root = _mainUi;
        _view.UseDialogs(_mainMenu, _mainPrompt);
        _view.Floating = false;
        _view.IsShown = _shownDocked;
        _console.AttachVisualizer();
        _mainUi.PointerHooks.Add(_view);
        _mainRenderer.Scene = _view;
        _log("3D Visualizer docked.");
    }

    /// <summary>
    /// Each frame: the window drawn, or docked once it's been closed.
    /// </summary>
    public void Frame()
    {
        if (_window is not { } window)
            return;
        if (window.ShouldClose)
        {
            Dock();
            return;
        }
        var size = window.FramebufferSize;
        if (window.IsIconified || size.X == 0 || size.Y == 0)
            return;
        var screen = new float2(size.X, size.Y);
        _view.Available = screen;
        _view.Update();
        _menu!.ScreenSize = _prompt!.ScreenSize = screen;
        _ui!.Layout(screen);
        _input!.UpdateCursor();
        _renderer!.DrawFrame(_ui);
    }

    /// <summary>
    /// Keeps where the visualizer is for next time.
    /// </summary>
    public void SaveState(bool? floating = null)
    {
        var open = floating ?? _window is not null;
        (int, int, int, int)? rect = _window is { } w ? (w.Position.X, w.Position.Y, w.Size.X, w.Size.Y) : null;
        new VizWindowState(open, rect, _view.UiVisible).Save(_statePath);
    }

    public void Dispose()
    {
        if (_window is null)
            return;
        _renderer?.Dispose();
        _input?.Dispose();
        _window.Dispose();
        _window = null;
    }
}
