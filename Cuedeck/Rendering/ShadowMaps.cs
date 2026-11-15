using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Cuedeck.Visualizer;

namespace Cuedeck.Rendering;

/// <summary>
/// The 3D view's shadow maps: a depth image per shadow-casting light (<see cref="SceneFrame.MAX_SHADOWS"/> layers
/// of one array), each drawn from its light, and a comparing sampler that filters their edges. Left ready to read
/// between frames, so the scene's shader can always sample them.
/// </summary>
sealed unsafe class ShadowMaps : IDisposable
{
    public const int SIZE = 1024;
    public const VkFormat FORMAT = VkFormat.D32SFloat;

    readonly GpuContext _gpu;
    readonly VmaAllocation _allocation;
    readonly VkImageView[] _layers = new VkImageView[SceneFrame.MAX_SHADOWS];

    public VkImage Image { get; }

    /// <summary>
    /// Every layer, for the shader.
    /// </summary>
    public VkImageView ArrayView { get; }

    public VkSampler Sampler { get; }

    /// <summary>
    /// Layer <paramref name="i"/> alone, to draw into.
    /// </summary>
    public VkImageView Layer(int i) => _layers[i];

    public ShadowMaps(GpuContext gpu)
    {
        _gpu = gpu;
        gpu.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = FORMAT,
                Extent = new VkExtent3D { Width = SIZE, Height = SIZE, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = SceneFrame.MAX_SHADOWS,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out var image,
            out _allocation,
            out _
        );
        Image = image;
        VkImageView View(VkImageViewType type, int baseLayer, int count) =>
            gpu.Device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = image,
                    ViewType = type,
                    Format = FORMAT,
                    SubresourceRange = new VkImageSubresourceRange
                    {
                        AspectMask = VkImageAspectFlags.DepthBit,
                        LevelCount = 1,
                        BaseArrayLayer = baseLayer,
                        LayerCount = count,
                    },
                },
                null
            );
        ArrayView = View(VkImageViewType._2DArray, 0, SceneFrame.MAX_SHADOWS);
        for (var i = 0; i < _layers.Length; i++)
            _layers[i] = View(VkImageViewType._2D, i, 1);
        // Beyond a map's edge is lit; its edges filtered by comparing four texels.
        Sampler = gpu.Device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToBorder,
                AddressModeV = VkSamplerAddressMode.ClampToBorder,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                BorderColor = VkBorderColor.FloatOpaqueWhite,
                CompareEnable = true,
                CompareOp = VkCompareOp.LessOrEqual,
                MaxLod = 1,
            },
            null
        );
        gpu.SubmitAndWait(cmd =>
            cmd.TransitionImage(
                image,
                VkImageAspectFlags.DepthBit,
                VkImageLayout.Undefined,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TopOfPipeBit,
                VkAccessFlags.None,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit,
                layerCount: SceneFrame.MAX_SHADOWS
            )
        );
    }

    public void Dispose()
    {
        _gpu.Device.DestroySampler(Sampler, null);
        foreach (var v in _layers)
            _gpu.Device.DestroyImageView(v, null);
        _gpu.Device.DestroyImageView(ArrayView, null);
        _gpu.Allocator.DestroyImage(Image, _allocation);
    }
}
