using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Cuedeck.UI;

namespace Cuedeck.Rendering;

/// <summary>
/// The gobos the 3D view's lights project, as layers of one texture array: each gobo's artwork (a profile's base64
/// PNG) decoded and scaled to <see cref="SIZE"/> square the first time a light shows it. When every layer's taken,
/// the array starts again.
/// </summary>
sealed unsafe class GoboArray : IDisposable
{
    public const int SIZE = 256;
    public const int LAYERS = 16;

    readonly GpuContext _gpu;
    readonly VmaAllocation _allocation;
    readonly Dictionary<string, int> _layers = [];

    public VkImage Image { get; }
    public VkImageView View { get; }
    public VkSampler Sampler { get; }

    public GoboArray(GpuContext gpu)
    {
        _gpu = gpu;
        gpu.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R8G8B8A8SRGB,
                Extent = new VkExtent3D { Width = SIZE, Height = SIZE, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = LAYERS,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out var image,
            out _allocation,
            out _
        );
        Image = image;
        View = gpu.Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2DArray,
                Format = VkFormat.R8G8B8A8SRGB,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = LAYERS,
                },
            },
            null
        );
        Sampler = gpu.Device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
        // Black to begin with, ready to read.
        gpu.SubmitAndWait(cmd =>
        {
            cmd.TransitionImage(
                image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.Undefined,
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit,
                VkAccessFlags.None,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                layerCount: LAYERS
            );
            var black = new VkClearColorValue();
            cmd.ClearColorImage(
                image,
                VkImageLayout.TransferDstOptimal,
                &black,
                [
                    new VkImageSubresourceRange
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = LAYERS,
                    },
                ]
            );
            cmd.TransitionImage(
                image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferDstOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit,
                layerCount: LAYERS
            );
        });
    }

    /// <summary>
    /// The layer holding gobo <paramref name="base64"/> (uploaded now if it's new, waiting for the GPU to finish with
    /// the array first), or -1 for none or artwork that won't decode. Call outside any rendering pass.
    /// </summary>
    public int LayerOf(string base64)
    {
        if (base64 == "")
            return -1;
        if (_layers.TryGetValue(base64, out var layer))
            return layer;
        Png.Image picture;
        try
        {
            picture = Png.Resize(Png.Decode(Convert.FromBase64String(base64)), SIZE, SIZE);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            _layers[base64] = -1;
            return -1;
        }
        if (_layers.Count(kv => kv.Value >= 0) >= LAYERS)
            _layers.Clear();
        layer = Enumerable.Range(0, LAYERS).First(l => !_layers.ContainsValue(l));
        _layers[base64] = layer;

        _gpu.Device.WaitIdle();
        using var staging = _gpu.CreateStagingBuffer<byte>(picture.Pixels);
        var l = layer;
        _gpu.SubmitAndWait(cmd =>
        {
            cmd.TransitionImage(
                Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                baseLayer: l
            );
            cmd.CopyBufferToImage(
                staging.Buffer,
                Image,
                VkImageLayout.TransferDstOptimal,
                [
                    new VkBufferImageCopy
                    {
                        ImageSubresource = new VkImageSubresourceLayers
                        {
                            AspectMask = VkImageAspectFlags.ColorBit,
                            BaseArrayLayer = l,
                            LayerCount = 1,
                        },
                        ImageExtent = new VkExtent3D { Width = SIZE, Height = SIZE, Depth = 1 },
                    },
                ]
            );
            cmd.TransitionImage(
                Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.TransferDstOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit,
                VkAccessFlags.TransferWriteBit,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit,
                baseLayer: l
            );
        });
        return layer;
    }

    public void Dispose()
    {
        _gpu.Device.DestroySampler(Sampler, null);
        _gpu.Device.DestroyImageView(View, null);
        _gpu.Allocator.DestroyImage(Image, _allocation);
    }
}
