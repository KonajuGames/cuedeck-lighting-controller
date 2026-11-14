using Brutal.Numerics;
using Cuedeck.Visualizer;

namespace Cuedeck.Rendering;

/// <summary>
/// What the renderer's 3D pass draws for: the 3D Visualizer's view.
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
}
