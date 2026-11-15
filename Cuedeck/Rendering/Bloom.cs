using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Cuedeck.Rendering;

/// <summary>
/// The 3D Visualizer's glow on bright beams, lenses and pools (Godot's, with sLight's settings): the scene's HDR picture's
/// bright parts picked out at half size, shrunk to a quarter, an eighth and a sixteenth, those three weighted and
/// added back together (Godot's glow levels 2 to 4), and laid over the picture with a Screen blend as both are tone
/// mapped into the image the UI shows. Each step a draw of <c>bloom.frag</c> over the whole of its target.
/// </summary>
sealed unsafe class Bloom : IDisposable
{
    public const VkFormat FORMAT = VkFormat.R16G16B16A16SFloat;

    /// <summary>
    /// sLight's <c>glow_hdr_threshold</c>, <c>glow_intensity</c> and <c>glow_bloom</c>, and Godot's defaults for the
    /// rest: the HDR scale, the luminance cap, and levels 2, 3 and 4's weights.
    /// </summary>
    const float THRESHOLD = 0.9f,
        INTENSITY = 0.5f,
        BLOOM = 0.08f,
        HDR_SCALE = 2,
        CAP = 12,
        LEVEL2 = 0.8f,
        LEVEL3 = 0.4f,
        LEVEL4 = 0.1f;

    /// <summary>
    /// Matches <c>bloom.frag</c>'s push constants.
    /// </summary>
    struct Push
    {
        public Vector4 Mode;
        public Vector4 Bright;
        public Vector4 Weights;
    }

    /// <summary>
    /// The passes, in the order they're drawn: each one's target and what it reads.
    /// </summary>
    enum Pass
    {
        Half,
        Quarter,
        Eighth,
        Sixteenth,
        UpEighth,
        UpQuarter,
        Composite,
    }

    const int PASSES = 7;

    readonly GpuContext _gpu;
    readonly DeviceEx _device;
    readonly VkDescriptorSetLayout _setLayout;
    readonly VkDescriptorPool _pool;
    readonly VkDescriptorSet[] _sets = new VkDescriptorSet[PASSES];
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _glowPipeline;
    readonly VkPipeline _compositePipeline;
    readonly VkSampler _sampler;

    /// <summary>
    /// Half, quarter, eighth and sixteenth size; then the eighth and quarter with the smaller ones added in.
    /// </summary>
    readonly GpuImage?[] _levels = new GpuImage?[6];

    public Bloom(GpuContext gpu, VkFormat outputFormat)
    {
        _gpu = gpu;
        _device = gpu.Device;
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new()
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new()
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        };
        _setLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings }, null);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * PASSES };
        _pool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = PASSES, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
        var layout = _setLayout;
        Span<VkDescriptorSet> set = stackalloc VkDescriptorSet[1];
        for (var i = 0; i < PASSES; i++)
        {
            _device.AllocateDescriptorSets(
                new VkDescriptorSetAllocateInfo
                {
                    DescriptorPool = _pool,
                    DescriptorSetCount = 1,
                    SetLayouts = &layout,
                },
                set
            );
            _sets[i] = set[0];
        }
        _layout = _device.CreatePipelineLayout([_setLayout], [new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Size = ByteSize.Of<Push>() }], null);
        _glowPipeline = CreatePipeline(FORMAT);
        _compositePipeline = CreatePipeline(outputFormat);
        _sampler = _device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
    }

    VkPipeline CreatePipeline(VkFormat format)
    {
        using var stages = ShaderLoader.Load(_device, "post.vert", "bloom.frag");
        var noVertices = new VkPipelineVertexInputStateCreateInfo();
        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
        var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &format };
        return _device.CreateGraphicsPipeline(
            VkPipelineCache.NullHandle,
            new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = stages.Count,
                Stages = stages,
                VertexInputState = &noVertices,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                ViewportState = &viewportState,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = Presets.DepthStencil.NoDepthTest,
                ColorBlendState = Presets.BlendState.BlendNone,
                DynamicState = &dynamicState,
                Layout = _layout,
            },
            null
        );
    }

    /// <summary>
    /// Makes the smaller images for a picture <paramref name="size"/> pixels big, reading <paramref name="hdr"/>. Only
    /// while the GPU's idle.
    /// </summary>
    public void Resize(VkExtent2D size, VkImageView hdr)
    {
        foreach (var l in _levels)
            l?.Dispose();
        VkExtent2D Scaled(int shift) => new(Math.Max(1, size.Width >> shift), Math.Max(1, size.Height >> shift));
        GpuImage Make(int shift) =>
            new(_gpu, FORMAT, Scaled(shift), 1, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        _levels[0] = Make(1);
        _levels[1] = Make(2);
        _levels[2] = Make(3);
        _levels[3] = Make(4);
        _levels[4] = Make(3);
        _levels[5] = Make(2);
        var v = _levels.Select(l => l!.View).ToArray();
        // What each pass reads: the source (or the smaller image) and the image its own size.
        (VkImageView, VkImageView)[] reads =
        [
            (hdr, hdr),
            (v[0], v[0]),
            (v[1], v[1]),
            (v[2], v[2]),
            (v[3], v[2]),
            (v[4], v[1]),
            (v[5], hdr),
        ];
        for (var i = 0; i < PASSES; i++)
        {
            var a = new VkDescriptorImageInfo { Sampler = _sampler, ImageView = reads[i].Item1, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var b = new VkDescriptorImageInfo { Sampler = _sampler, ImageView = reads[i].Item2, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = &a,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = &b,
                    },
                ],
                []
            );
        }
    }

    /// <summary>
    /// Draws the glow from the HDR picture (ready to read) and lays it over the picture into <paramref name="output"/>
    /// (already a colour attachment), which it leaves as one.
    /// </summary>
    public void Record(CommandBuffer cmd, VkImageView output, VkExtent2D outputSize)
    {
        if (_levels[0] is null)
            return;
        var bright = new Vector4(THRESHOLD, HDR_SCALE, BLOOM, CAP);
        Draw(cmd, Pass.Half, new Push { Mode = new(0), Bright = bright });
        Draw(cmd, Pass.Quarter, new Push { Mode = new(1) });
        Draw(cmd, Pass.Eighth, new Push { Mode = new(1) });
        Draw(cmd, Pass.Sixteenth, new Push { Mode = new(1) });
        Draw(cmd, Pass.UpEighth, new Push { Mode = new(2), Weights = new(LEVEL4, LEVEL3, 0, 0) });
        Draw(cmd, Pass.UpQuarter, new Push { Mode = new(2), Weights = new(1, LEVEL2, 0, 0) });
        Draw(cmd, Pass.Composite, new Push { Mode = new(3), Weights = new(0, 0, INTENSITY, 0) }, (output, outputSize));
    }

    void Draw(CommandBuffer cmd, Pass pass, Push push, (VkImageView View, VkExtent2D Size)? output = null)
    {
        var target = output is null ? _levels[(int)pass]! : null;
        var view = target?.View ?? output!.Value.View;
        var size = target?.Extent ?? output!.Value.Size;
        if (target is not null)
            cmd.TransitionImage(
                target.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.Undefined,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.None,
                VkPipelineStageFlags.ColorAttachmentOutputBit,
                VkAccessFlags.ColorAttachmentWriteBit
            );
        var attachment = new VkRenderingAttachmentInfo
        {
            ImageView = view,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.DontCare,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(size),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &attachment,
            }
        );
        cmd.SetViewport(0, [new VkViewport { Width = size.Width, Height = size.Height, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D(size)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pass == Pass.Composite ? _compositePipeline : _glowPipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[(int)pass]], []);
        cmd.PushConstants(_layout, VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRendering();
        if (target is not null)
            cmd.TransitionImage(
                target.Image,
                VkImageAspectFlags.ColorBit,
                VkImageLayout.ColorAttachmentOptimal,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.ColorAttachmentOutputBit,
                VkAccessFlags.ColorAttachmentWriteBit,
                VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.ShaderReadBit
            );
    }

    public void Dispose()
    {
        foreach (var l in _levels)
            l?.Dispose();
        _device.DestroySampler(_sampler, null);
        _device.DestroyPipeline(_glowPipeline, null);
        _device.DestroyPipeline(_compositePipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
