using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Cuedeck.UI;

namespace Cuedeck.Rendering;

/// <summary>
/// Clears a window and draws the UI over it, using dynamic rendering and keeping two frames in flight: the main
/// window's, or another's (the visualizer popped out) on a surface of its own.
/// </summary>
sealed unsafe class Renderer : IDisposable
{
    const int FRAMES_IN_FLIGHT = 2;

    /// <summary>
    /// What one frame in flight owns. The CPU records frame N+1 into the other slot while the GPU draws frame N.
    /// </summary>
    struct Frame
    {
        public CommandBuffer CommandBuffer;
        public VkSemaphore ImageAvailable;
        public VkFence InFlight;
    }

    static readonly float4 ClearColor = new(0.02f, 0.02f, 0.03f, 1.0f);

    readonly GpuContext _gpu;
    readonly GlfwWindow _window;
    readonly DeviceEx _device;

    Swapchain _swapchain;
    bool _swapchainStale;

    readonly OverlayRenderer _ui;
    readonly Scene3D _scene;

    /// <summary>
    /// The 3D view drawn before the UI each frame, if it's on screen.
    /// </summary>
    public IScene3DSource? Scene { get; set; }

    readonly VkCommandPool _commandPool;
    readonly Frame[] _frames = new Frame[FRAMES_IN_FLIGHT];
    int _frameIndex;

    readonly VkSurfaceKHR _surface;
    readonly bool _ownSurface;

    /// <summary>
    /// Set to copy the next frame out, whole, for <see cref="TakeWindowCapture"/> (the screenshot tool); it's
    /// cleared once the frame's drawn. The GPU's finished with the frame before it's read, so it stalls a moment.
    /// </summary>
    public bool CaptureWindow { get; set; }

    GpuBuffer? _windowReadback;
    (int Width, int Height, byte[] Rgba)? _windowShot;

    /// <summary>
    /// The frame copied out after <see cref="CaptureWindow"/>, as sRGB RGBA rows from the top; null until there is
    /// one (or if the window can't be copied).
    /// </summary>
    public (int Width, int Height, byte[] Rgba)? TakeWindowCapture()
    {
        var shot = _windowShot;
        _windowShot = null;
        return shot;
    }

    /// <param name="surface">Another window's surface, made for it (<see cref="GpuContext.CreateSurface"/>), which
    /// the renderer then owns; null for the main window's.</param>
    public Renderer(GpuContext gpu, GlfwWindow window, FontAtlas fonts, VkSurfaceKHR? surface = null)
    {
        _gpu = gpu;
        _window = window;
        _device = gpu.Device;
        _surface = surface ?? gpu.Surface;
        _ownSurface = surface is not null;

        _swapchain = new Swapchain(gpu, _surface, window.FramebufferSize, null);
        _ui = new OverlayRenderer(gpu, fonts, _swapchain.Format, FRAMES_IN_FLIGHT);
        _scene = new Scene3D(gpu, FRAMES_IN_FLIGHT);

        _commandPool = _device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = gpu.QueueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            },
            null
        );
        for (var i = 0; i < FRAMES_IN_FLIGHT; i++)
            _frames[i] = new Frame
            {
                CommandBuffer = _device.AllocateCommandBuffer(
                    new VkCommandBufferAllocateInfo { CommandPool = _commandPool, Level = VkCommandBufferLevel.Primary }
                ),
                ImageAvailable = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
                InFlight = _device.CreateFence(VkFenceCreateFlags.SignaledBit, null),
            };

        // A resize only marks the swapchain stale; it's rebuilt before the next frame is acquired.
        _window.OnFramebufferSize += (_, _) => _swapchainStale = true;
    }

    /// <summary>
    /// Waits for the GPU to finish everything and builds a swapchain at the window's current size. Returns false,
    /// leaving the swapchain stale, while the surface has no area: just after a restore from minimised, Windows can
    /// still report a 0x0 surface even though GLFW reports a framebuffer size.
    /// </summary>
    bool TryRecreateSwapchain()
    {
        var surfaceExtent = _gpu.PhysicalDevice.GetSurfaceCapabilitiesKHR(_surface).CurrentExtent;
        if (surfaceExtent.Width == 0 || surfaceExtent.Height == 0)
            return false;

        _device.WaitIdle();
        var old = _swapchain;
        _swapchain = new Swapchain(_gpu, _surface, _window.FramebufferSize, old);
        old.Dispose();
        _swapchainStale = false;
        return true;
    }

    /// <summary>
    /// Records, submits and presents one frame. Call only while the window has a non-zero framebuffer.
    /// </summary>
    public void DrawFrame(Root ui)
    {
        if (_swapchainStale && !TryRecreateSwapchain())
            return;

        ref var frame = ref _frames[_frameIndex];
        _device.WaitForFence(frame.InFlight, -1);

        int imageIndex;
        try
        {
            var acquired = _device.AcquireNextImageKHR(
                _swapchain.Handle,
                -1,
                frame.ImageAvailable,
                VkFence.NullHandle,
                out imageIndex
            );
            // Still usable this frame; rebuild before the next one.
            if (acquired == VkResult.SuboptimalKHR)
                _swapchainStale = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            // Nothing was acquired, so the semaphore is untouched and the fence is still signalled: retry next frame.
            _swapchainStale = true;
            return;
        }

        // Only reset once work is certain to be submitted, or the next wait on this fence would hang.
        _device.ResetFences([frame.InFlight]);

        var commandBuffer = frame.CommandBuffer;
        commandBuffer.Reset();
        commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var capture = CaptureWindow && _swapchain.CanCopy;
        Record(commandBuffer, imageIndex, ui, capture);
        commandBuffer.End();

        var renderFinished = _swapchain.RenderFinished[imageIndex];
        _gpu.Queue.Submit(
            [frame.ImageAvailable],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [commandBuffer],
            [renderFinished],
            frame.InFlight
        );

        var swapchainHandle = _swapchain.Handle;
        try
        {
            var presented = _gpu.Queue.PresentKHR(
                new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &renderFinished,
                    SwapchainCount = 1,
                    Swapchains = &swapchainHandle,
                    ImageIndices = &imageIndex,
                }
            );
            if (presented == VkResult.SuboptimalKHR)
                _swapchainStale = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            _swapchainStale = true;
        }

        if (CaptureWindow)
        {
            CaptureWindow = false;
            if (capture)
                ReadWindow();
        }
        _frameIndex = (_frameIndex + 1) % FRAMES_IN_FLIGHT;
    }

    /// <summary>
    /// Reads back the frame just copied out, once the GPU's done with it.
    /// </summary>
    void ReadWindow()
    {
        _device.WaitIdle();
        var extent = _swapchain.Extent;
        var bytes = new byte[extent.Width * extent.Height * 4];
        _gpu.Allocator.InvalidateAllocation(_windowReadback!.Allocation, ByteSize.Zero, ByteSize.Of<byte>() * bytes.Length);
        new ReadOnlySpan<byte>((void*)_windowReadback.Info.PMappedData, bytes.Length).CopyTo(bytes);
        if (_swapchain.Format is VkFormat.B8G8R8A8SRGB or VkFormat.B8G8R8A8UNorm)
            for (var i = 0; i < bytes.Length; i += 4)
                (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        for (var i = 3; i < bytes.Length; i += 4)
            bytes[i] = 255;
        _windowShot = (extent.Width, extent.Height, bytes);
    }

    void Record(CommandBuffer cmd, int imageIndex, Root ui, bool capture)
    {
        var image = _swapchain.Images[imageIndex];
        var extent = _swapchain.Extent;

        // The 3D view's picture first, for the UI to show.
        // A picture copied out last time this slot was used is ready now its frame's done.
        if (_scene.TakeCaptured(_frameIndex) is { } shot)
            Scene?.Captured(shot.Width, shot.Height, shot.Rgba);
        if (Scene?.SceneSize is { } size)
        {
            if (_scene.EnsureSize(new VkExtent2D((int)size.X, (int)size.Y)))
                _ui.SetTexture(Scene3D.TEXTURE, _scene.View!.Value);
            _scene.Record(cmd, _frameIndex, Scene.BuildScene(), Scene.CaptureThisFrame());
        }

        // Previous contents are discarded (Undefined) since the image is cleared.
        cmd.TransitionImage(
            image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.Undefined,
            VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.None,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit
        );

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = _swapchain.ImageViews[imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = ClearColor },
        };

        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
            }
        );

        _ui.Record(cmd, _frameIndex, extent, ui);

        cmd.EndRendering();

        if (capture)
        {
            var bytes = extent.Width * extent.Height * 4;
            if (_windowReadback is null || (long)_windowReadback.Size < bytes)
            {
                _windowReadback?.Dispose();
                _windowReadback = GpuBuffer.Create(
                    _gpu.Allocator,
                    ByteSize.Of<byte>() * bytes,
                    VkBufferUsageFlags.TransferDstBit,
                    VmaAllocationCreateFlags.HostAccessRandomBit | VmaAllocationCreateFlags.MappedBit
                );
            }
            cmd.TransitionImage(
                image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags.ColorAttachmentOutputBit,
                VkAccessFlags.ColorAttachmentWriteBit,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferReadBit
            );
            cmd.CopyImageToBuffer(
                image,
                VkImageLayout.TransferSrcOptimal,
                _windowReadback.Buffer,
                [
                    new VkBufferImageCopy
                    {
                        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                        ImageExtent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                    },
                ]
            );
            cmd.TransitionImage(
                image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferSrcOptimal,
                VkImageLayout.PresentSrcKHR,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferReadBit,
                VkPipelineStageFlags.BottomOfPipeBit,
                VkAccessFlags.None
            );
            return;
        }

        cmd.TransitionImage(
            image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.ColorAttachmentOptimal,
            VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkAccessFlags.None
        );
    }

    public void Dispose()
    {
        _device.WaitIdle();

        foreach (var frame in _frames)
        {
            _device.DestroyFence(frame.InFlight, null);
            _device.DestroySemaphore(frame.ImageAvailable, null);
        }
        _device.DestroyCommandPool(_commandPool, null);

        _windowReadback?.Dispose();
        _scene.Dispose();
        _ui.Dispose();
        _swapchain.Dispose();
        if (_ownSurface)
            _gpu.DestroySurface(_surface);
    }
}
