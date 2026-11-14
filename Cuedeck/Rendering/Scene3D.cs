using System.Numerics;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Cuedeck.Visualizer;

namespace Cuedeck.Rendering;

/// <summary>
/// The 3D Visualizer's pass: draws a <see cref="SceneFrame"/> into an offscreen colour image (with its own depth
/// buffer), which the UI then shows as texture <see cref="TEXTURE"/> (<see cref="UI.Batch.AddTexture"/>). One lit,
/// opaque pipeline: every shape a mesh made once, placed by its instance's matrices from a storage buffer.
/// </summary>
sealed unsafe class Scene3D : IDisposable
{
    /// <summary>
    /// The UI's number for the scene's image.
    /// </summary>
    public const int TEXTURE = 1;

    const VkFormat COLOR = VkFormat.R8G8B8A8SRGB;
    const VkFormat DEPTH = VkFormat.D32SFloat;

    struct Push
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 Ambient;
        public Vector4 WorkDir;
        public Vector4 WorkColour;
    }

    struct Instance
    {
        public Matrix4x4 Model;
        public Matrix4x4 Normal;
        public Vector4 Colour;
        public Vector4 Extra;
    }

    struct MeshVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
    }

    sealed class FrameInstances
    {
        public GpuBuffer? Buffer;
        public int Capacity;
        public VkDescriptorSet Set;
    }

    readonly GpuContext _gpu;
    readonly DeviceEx _device;
    readonly VkDescriptorSetLayout _setLayout;
    readonly VkDescriptorPool _pool;
    readonly VkPipelineLayout _pipelineLayout;
    readonly VkPipeline _pipeline;
    readonly GpuBuffer _vertices;
    readonly GpuBuffer _indices;
    readonly Dictionary<MeshKind, (int FirstIndex, int IndexCount, int VertexOffset)> _meshes = [];
    readonly FrameInstances[] _frames;
    GpuImage? _color;
    GpuImage? _depth;

    public Scene3D(GpuContext gpu, int framesInFlight)
    {
        _gpu = gpu;
        _device = gpu.Device;

        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit,
        };
        _setLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = framesInFlight };
        _pool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = framesInFlight, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
        _frames = new FrameInstances[framesInFlight];
        var layout = _setLayout;
        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
        for (var i = 0; i < framesInFlight; i++)
        {
            _device.AllocateDescriptorSets(
                new VkDescriptorSetAllocateInfo
                {
                    DescriptorPool = _pool,
                    DescriptorSetCount = 1,
                    SetLayouts = &layout,
                },
                sets
            );
            _frames[i] = new FrameInstances { Set = sets[0] };
        }

        _pipelineLayout = _device.CreatePipelineLayout(
            [_setLayout],
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = ByteSize.Of<Push>() }],
            null
        );
        _pipeline = CreatePipeline();

        // Every shape in one vertex and one index buffer.
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        foreach (var kind in Enum.GetValues<MeshKind>())
        {
            var m = MeshData.Of(kind);
            _meshes[kind] = (indices.Count, m.Indices.Count, vertices.Count);
            for (var i = 0; i < m.Positions.Count; i++)
                vertices.Add(new MeshVertex { Position = m.Positions[i], Normal = m.Normals[i] });
            indices.AddRange(m.Indices);
        }
        _vertices = Upload<MeshVertex>([.. vertices], VkBufferUsageFlags.VertexBufferBit);
        _indices = Upload<uint>([.. indices], VkBufferUsageFlags.IndexBufferBit);
    }

    GpuBuffer Upload<T>(T[] data, VkBufferUsageFlags usage)
        where T : unmanaged
    {
        var buffer = GpuBuffer.Create(
            _gpu.Allocator,
            ByteSize.Of<T>() * data.Length,
            usage,
            VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
        );
        data.AsSpan().CopyTo(new Span<T>((void*)buffer.Info.PMappedData, data.Length));
        _gpu.Allocator.FlushAllocation(buffer.Allocation, ByteSize.Zero, ByteSize.Of<T>() * data.Length);
        return buffer;
    }

    VkPipeline CreatePipeline()
    {
        using var stages = ShaderLoader.Load(_device, "scene.vert", "scene.frag");
        var vertexInput = new VertexInput(1, 2)
            .AddBinding(0, ByteSize.Of<MeshVertex>(), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
            .AddAttribute(1, 0, VkFormat.R32G32B32SFloat, ByteSize.Of<Vector3>());
        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
        var colorFormat = COLOR;
        var rendering = new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount = 1,
            ColorAttachmentFormats = &colorFormat,
            DepthAttachmentFormat = DEPTH,
        };
        return _device.CreateGraphicsPipeline(
            VkPipelineCache.NullHandle,
            new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = stages.Count,
                Stages = stages,
                VertexInputState = vertexInput,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                ViewportState = &viewportState,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = Presets.DepthStencil.DepthTestWrite,
                ColorBlendState = Presets.BlendState.BlendNone,
                DynamicState = &dynamicState,
                Layout = _pipelineLayout,
            },
            null
        );
    }

    /// <summary>
    /// The image the scene's drawn into, once there is one.
    /// </summary>
    public VkImageView? View => _color?.View;

    /// <summary>
    /// Makes the images <paramref name="size"/> pixels big, if they aren't already (waiting for the GPU to finish
    /// with the old ones). Returns whether they were made anew, so the UI's texture needs pointing at them.
    /// </summary>
    public bool EnsureSize(VkExtent2D size)
    {
        if (_color is { } c && c.Extent.Width == size.Width && c.Extent.Height == size.Height)
            return false;
        _device.WaitIdle();
        _color?.Dispose();
        _depth?.Dispose();
        _color = new GpuImage(_gpu, COLOR, size, 1, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        _depth = new GpuImage(_gpu, DEPTH, size, 1, VkImageUsageFlags.DepthStencilAttachmentBit, VkImageAspectFlags.DepthBit);
        return true;
    }

    /// <summary>
    /// Draws <paramref name="frame"/> into the image, leaving it ready for the UI to read. Outside any rendering pass;
    /// <paramref name="frameIndex"/>'s previous use must have finished on the GPU.
    /// </summary>
    public void Record(CommandBuffer cmd, int frameIndex, SceneFrame frame)
    {
        if (_color is null || _depth is null)
            return;
        var extent = _color.Extent;
        var instances = _frames[frameIndex];
        var count = frame.Items.Count;
        if (instances.Buffer is null || instances.Capacity < count)
        {
            instances.Buffer?.Dispose();
            instances.Capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 256));
            instances.Buffer = GpuBuffer.Create(
                _gpu.Allocator,
                ByteSize.Of<Instance>() * instances.Capacity,
                VkBufferUsageFlags.StorageBufferBit,
                VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
            );
            var info = new VkDescriptorBufferInfo { Buffer = instances.Buffer.Buffer, Range = ByteSize.Of<Instance>() * instances.Capacity };
            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = instances.Set,
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &info,
                    },
                ],
                []
            );
        }
        var mapped = new Span<Instance>((void*)instances.Buffer.Info.PMappedData, count);
        for (var i = 0; i < count; i++)
        {
            var it = frame.Items[i];
            Matrix4x4.Invert(it.World, out var inverse);
            mapped[i] = new Instance
            {
                Model = it.World,
                Normal = Matrix4x4.Transpose(inverse),
                Colour = new Vector4(it.Colour.X, it.Colour.Y, it.Colour.Z, it.Colour.W),
                Extra = new Vector4(it.Glow, 0, 0, 0),
            };
        }
        if (count > 0)
            _gpu.Allocator.FlushAllocation(instances.Buffer.Allocation, ByteSize.Zero, ByteSize.Of<Instance>() * count);

        // The UI may still be reading last frame's picture: wait for that, then draw over it.
        cmd.TransitionImage(
            _color.Image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.Undefined,
            VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.None,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit
        );
        cmd.TransitionImage(
            _depth.Image,
            VkImageAspectFlags.DepthBit,
            VkImageLayout.Undefined,
            VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentReadBit
        );

        var bg = Tonemap(frame.Background);
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = _color.View,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = new float4(bg.X, bg.Y, bg.Z, 1) },
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = _depth.View,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1 } },
        };
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthAttachment = &depthAttachment,
            }
        );
        // Flipped, so the projection's Y up comes out up.
        cmd.SetViewport(
            0,
            [
                new VkViewport
                {
                    Y = extent.Height,
                    Width = extent.Width,
                    Height = -(float)extent.Height,
                    MaxDepth = 1,
                },
            ]
        );
        cmd.SetScissor(0, [new VkRect2D(extent)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, [instances.Set], []);
        var work = SceneFrame.WorkLightDirection;
        cmd.PushConstants(
            _pipelineLayout,
            VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero,
            new Push
            {
                ViewProj = frame.View * frame.Projection,
                CameraPos = new Vector4(frame.CameraPosition, 1),
                Ambient = new Vector4(SceneFrame.Ambient.X, SceneFrame.Ambient.Y, SceneFrame.Ambient.Z, frame.HazeDensity),
                WorkDir = new Vector4(work, 0),
                WorkColour = new Vector4(frame.WorkLight.X, frame.WorkLight.Y, frame.WorkLight.Z, 0),
            }
        );
        cmd.BindVertexBuffers(0, [_vertices.Buffer], [new ByteSize64(0)]);
        cmd.BindIndexBuffer(_indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
        for (var i = 0; i < count; i++)
        {
            var (first, n, offset) = _meshes[frame.Items[i].Mesh];
            cmd.DrawIndexed(n, 1, first, offset, i);
        }
        cmd.EndRendering();

        cmd.TransitionImage(
            _color.Image,
            VkImageAspectFlags.ColorBit,
            VkImageLayout.ColorAttachmentOptimal,
            VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ColorAttachmentWriteBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderReadBit
        );
    }

    /// <summary>
    /// The backdrop through the same highlight roll-off as the shapes.
    /// </summary>
    static Vector3 Tonemap(float4 c)
    {
        static float Roll(float x) => x <= 0.8f ? x : 0.8f + 0.2f * (1 - MathF.Exp(-(x - 0.8f) / 0.2f));
        return new Vector3(Roll(c.X), Roll(c.Y), Roll(c.Z));
    }

    public void Dispose()
    {
        foreach (var f in _frames)
            f.Buffer?.Dispose();
        _color?.Dispose();
        _depth?.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_pipelineLayout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
