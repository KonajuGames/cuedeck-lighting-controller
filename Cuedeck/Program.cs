using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Cuedeck;
using Cuedeck.Input;
using Cuedeck.Rendering;
using Cuedeck.UI;

WindowsShell.AttachParentConsole();

// What goes wrong is written down beside the settings, to look into after a show; even on another thread, where it
// still ends the app.
ErrorLog.FilePath = Path.Combine(AppPaths.Folder, "errors.log");
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception e)
        ErrorLog.Write("working in the background", e);
};

try
{
    if (!Glfw.Init())
    {
        var errorCode = Glfw.GetError(out var errorMessage);
        throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
    }

    // GLFW can't find Brutal's Vulkan loader by itself, so touching Vulkan first loads it.
    _ = Vulkan.GetInstanceProcAddr;

    try
    {
        Run();
    }
    finally
    {
        Glfw.Terminate();
    }
}
catch (Exception e)
{
    // A windowed app has nowhere to print an unhandled exception, so it would otherwise just vanish.
    ErrorLog.Write("running", e);
    WindowsShell.ShowFatalError(e);
    return 1;
}

return 0;

static void Run()
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    // Made hidden, and shown once the first frame is drawn, so Windows doesn't show it empty while the app gets going.
    Glfw.WindowHint(GlfwWindowHint.Visible, 0);
    Glfw.WindowHint(GlfwWindowHint.Maximized, 1);

    // Declared first so it's disposed last, after the GPU objects that draw to it.
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Cuedeck", Size = new int2(1366, 820) });
    window.SetSizeLimits(new int2(1024, 640), new int2(-1, -1));

#if DEBUG
    const bool ENABLE_VALIDATION = true;
#else
    const bool ENABLE_VALIDATION = false;
#endif

    var atlas = new FontAtlas();
    var regular = FindFont(bold: false);
    var bold = FindFont(bold: true);
    var fonts = new Fonts(
        Small: atlas.Add(regular, 12),
        Body: atlas.Add(regular, 14),
        Bold: atlas.Add(bold, 14),
        Heading: atlas.Add(bold, 18),
        Big: atlas.Add(bold, 26)
    );
    var images = Images.Load(atlas);

    // The logo on the taskbar and in Alt+Tab too. GLFW copies the pixels, so they needn't outlive the call.
    unsafe
    {
        var icon = Images.Read("logo-32.png");
        fixed (byte* pixels = icon.Pixels)
            window.SetIcon(1, new GlfwImage { Width = icon.Width, Height = icon.Height, Pixels = pixels });
    }

    using var gpu = new GpuContext(window, ENABLE_VALIDATION);
    using var renderer = new Renderer(gpu, window, atlas);

    var ui = new Root { TooltipFont = fonts.Body };
    var screen = ui.Add(new MainScreen(fonts, images));
    using var input = new GlfwUiInput(window, ui);

    // The window's own buttons, and dragging it by the title bar, which Windows does once told where it is.
    screen.TitleBar.IsMaximized = () => window.IsMaximised;
    screen.TitleBar.MinimizeClicked += window.Iconify;
    screen.TitleBar.MaximizeClicked += () =>
    {
        if (window.IsMaximised)
            window.Restore();
        else
            window.Maximize();
    };
    screen.TitleBar.CloseClicked += () => window.ShouldClose = true;
    if (OperatingSystem.IsWindows())
        WindowChrome.Install(window, point => screen.TitleBar.IsCaption(new float2(point.X, point.Y)));

    var windowShown = false;
    while (!window.ShouldClose)
    {
        // Minimised, there's nothing to draw. Whether it's minimised is asked after handling events, as one of them may
        // be the minimise itself: drawing into a minimised window leaves it black for good once it's restored.
        if (window.IsIconified)
            Glfw.WaitEventsTimeout(0.05);
        else
            Glfw.PollEvents();
        var size = window.FramebufferSize;
        if (window.IsIconified || size.X == 0 || size.Y == 0)
            continue;

        screen.ScreenSize = new float2(size.X, size.Y);
        screen.Update();
        ui.Layout(screen.ScreenSize);
        input.UpdateCursor();
        renderer.DrawFrame(ui);
        if (!windowShown)
            window.Visible = windowShown = true;
    }

    if (OperatingSystem.IsWindows())
        WindowChrome.Remove();
}

static string FindFont(bool bold) =>
    FontAtlas.FindSystemFont(bold)
    ?? FontAtlas.FindSystemFont()
    ?? throw new FileNotFoundException("No usable system font found; install DejaVu Sans or Liberation Sans");
