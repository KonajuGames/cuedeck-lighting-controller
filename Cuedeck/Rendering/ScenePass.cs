using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Cuedeck.Visualizer;

namespace Cuedeck.Rendering;

/// <summary>
/// What the 3D pass draws for: the 3D Visualizer's view.
/// </summary>
interface IScene3DSource
{
    /// <summary>
    /// How big the picture is wanted, in pixels; null while it isn't on screen (then nothing's drawn).
    /// </summary>
    float2? SceneSize { get; }

    /// <summary>
    /// The frame to draw, made as it's needed.
    /// </summary>
    SceneFrame BuildScene();

    /// <summary>
    /// Whether this frame's picture is wanted (a screenshot, a recording's frame); asked once a frame. It comes back
    /// through <see cref="Captured"/> a frame or two later.
    /// </summary>
    bool CaptureThisFrame();

    /// <summary>
    /// A picture asked for: sRGB RGBA, rows from the top.
    /// </summary>
    void Captured(int width, int height, byte[] rgba);
}

/// <summary>
/// A window's 3D pass, drawn before its UI each frame (its renderer's <see cref="Renderer.PrePass"/>): the 3D view's
/// picture, for the UI to show as texture <see cref="Scene3D.TEXTURE"/>. One for each window that can show the
/// visualizer, since each renderer has its own frames in flight.
/// </summary>
sealed class ScenePass(GpuContext gpu) : IPrePass, IDisposable
{
    readonly Scene3D _scene = new(gpu, Renderer.FRAMES_IN_FLIGHT);

    /// <summary>
    /// The 3D view drawn, if it's on screen in this window.
    /// </summary>
    public IScene3DSource? Source { get; set; }

    public void Record(CommandBuffer cmd, int frameIndex, OverlayRenderer ui)
    {
        // A picture copied out last time this slot was used is ready now its frame's done.
        if (_scene.TakeCaptured(frameIndex) is { } shot)
            Source?.Captured(shot.Width, shot.Height, shot.Rgba);
        if (Source?.SceneSize is { } size)
        {
            if (_scene.EnsureSize(new VkExtent2D((int)size.X, (int)size.Y)))
                ui.SetTexture(Scene3D.TEXTURE, _scene.View!.Value);
            _scene.Record(cmd, frameIndex, Source.BuildScene(), Source.CaptureThisFrame());
        }
    }

    public void Dispose()
    {
        gpu.Device.WaitIdle();
        _scene.Dispose();
    }
}
