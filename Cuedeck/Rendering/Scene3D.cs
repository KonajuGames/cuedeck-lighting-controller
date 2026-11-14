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
/// opaque pipeline: every shape a mesh made once, placed by its instance's matrices from a storage buffer, and lit by
/// the fixtures' spotlights from another, looped over in the fragment shader, projecting their gobos
/// (<see cref="GoboArray"/>). Then the beams, added over it without writing depth.
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

    struct Light
    {
        public Vector4 Position;
        public Vector4 Direction;
        public Vector4 Colour;
        public Vector4 Extra;
        public Vector4 Right;
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
        public GpuBuffer? Lights;
        public int LightCapacity;
        public VkDescriptorSet Set;
    }

    readonly GpuContext _gpu;
    readonly DeviceEx _device;
    readonly VkDescriptorSetLayout _setLayout;
    readonly VkDescriptorPool _pool;
    readonly VkPipelineLayout _pipelineLayout;
    readonly VkPipeline _pipeline;
    readonly VkPipeline _beamPipeline;
    readonly GoboArray _gobos;
    readonly GpuBuffer _vertices;
    readonly GpuBuffer _indices;
    readonly Dictionary<MeshKind, (int FirstIndex, int IndexCount, int VertexOffset)> _meshes = [];

    /// <summary>
    /// Models' meshes (GDTF fixtures' parts, set pieces), each uploaded the first time it's drawn.
    /// </summary>
    readonly Dictionary<MeshData, (GpuBuffer Vertices, GpuBuffer Indices, int Count)> _models = new(ReferenceEqualityComparer.Instance);
    readonly FrameInstances[] _frames;
    GpuImage? _color;
    GpuImage? _depth;

    public Scene3D(GpuContext gpu, int framesInFlight)
    {
        _gpu = gpu;
        _device = gpu.Device;

        // The instances, for the vertex shader; the lights, for the fragment shader.
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new()
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit,
            },
            new()
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new()
            {
                Binding = 2,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        };
        _setLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 3, Bindings = bindings }, null);
        var poolSizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * framesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = framesInFlight },
        };
        _pool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = framesInFlight, PoolSizeCount = 2, PoolSizes = poolSizes }, null);
        _gobos = new GoboArray(gpu);
        var goboInfo = new VkDescriptorImageInfo
        {
            Sampler = _gobos.Sampler,
            ImageView = _gobos.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
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
            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = sets[0],
                        DstBinding = 2,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = &goboInfo,
                    },
                ],
                []
            );
        }

        _pipelineLayout = _device.CreatePipelineLayout(
            [_setLayout],
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = ByteSize.Of<Push>() }],
            null
        );
        _pipeline = CreatePipeline("scene.frag", beams: false);
        _beamPipeline = CreatePipeline("beam.frag", beams: true);

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

    /// <summary>
    /// The opaque pipeline, or the beams' (added over what's there, depth tested but not written).
    /// </summary>
    VkPipeline CreatePipeline(string fragment, bool beams)
    {
        using var stages = ShaderLoader.Load(_device, "scene.vert", fragment);
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
                DepthStencilState = beams ? Presets.DepthStencil.DepthTestNoWrite : Presets.DepthStencil.DepthTestWrite,
                ColorBlendState = beams ? Presets.BlendState.BlendAdditive : Presets.BlendState.BlendNone,
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
        // The shapes, then the beams after them.
        var count = frame.Items.Count;
        var all = count + frame.Beams.Count;
        var lightCount = frame.Lights.Count;
        if (instances.Buffer is null || instances.Capacity < all)
            (instances.Buffer, instances.Capacity) = Grow<Instance>(instances.Buffer, all, 256, instances.Set, 0);
        if (instances.Lights is null || instances.LightCapacity < lightCount)
            (instances.Lights, instances.LightCapacity) = Grow<Light>(instances.Lights, lightCount, 64, instances.Set, 1);
        var mapped = new Span<Instance>((void*)instances.Buffer.Info.PMappedData, all);
        for (var i = 0; i < all; i++)
        {
            var it = i < count ? frame.Items[i] : frame.Beams[i - count];
            Matrix4x4.Invert(it.World, out var inverse);
            mapped[i] = new Instance
            {
                Model = it.World,
                Normal = Matrix4x4.Transpose(inverse),
                Colour = new Vector4(it.Colour.X, it.Colour.Y, it.Colour.Z, it.Colour.W),
                Extra = new Vector4(it.Glow, 0, 0, 0),
            };
        }
        if (all > 0)
            _gpu.Allocator.FlushAllocation(instances.Buffer.Allocation, ByteSize.Zero, ByteSize.Of<Instance>() * all);
        var lights = new Span<Light>((void*)instances.Lights.Info.PMappedData, lightCount);
        for (var i = 0; i < lightCount; i++)
        {
            var l = frame.Lights[i];
            lights[i] = new Light
            {
                Position = new Vector4(l.Position, l.Range),
                Direction = new Vector4(l.Direction, l.CosCone),
                Colour = new Vector4(l.Colour, l.Decay),
                Extra = new Vector4(l.EdgeSoftness, 0, 0, 0),
                // w: the gobo's layer, or -1 for none.
                Right = new Vector4(l.Right, _gobos.LayerOf(l.Gobo)),
            };
        }
        if (lightCount > 0)
            _gpu.Allocator.FlushAllocation(instances.Lights.Allocation, ByteSize.Zero, ByteSize.Of<Light>() * lightCount);

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
                CameraPos = new Vector4(frame.CameraPosition, lightCount),
                Ambient = new Vector4(SceneFrame.Ambient.X, SceneFrame.Ambient.Y, SceneFrame.Ambient.Z, frame.HazeDensity),
                WorkDir = new Vector4(work, 0),
                WorkColour = new Vector4(frame.WorkLight.X, frame.WorkLight.Y, frame.WorkLight.Z, 0),
            }
        );
        cmd.BindVertexBuffers(0, [_vertices.Buffer], [new ByteSize64(0)]);
        cmd.BindIndexBuffer(_indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
        var shared = true;
        for (var i = 0; i < count; i++)
        {
            var item = frame.Items[i];
            if (item.Custom is { } custom)
            {
                if (ModelMesh(custom) is not { } m)
                    continue;
                cmd.BindVertexBuffers(0, [m.Vertices.Buffer], [new ByteSize64(0)]);
                cmd.BindIndexBuffer(m.Indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
                cmd.DrawIndexed(m.Count, 1, 0, 0, i);
                shared = false;
                continue;
            }
            if (!shared)
            {
                cmd.BindVertexBuffers(0, [_vertices.Buffer], [new ByteSize64(0)]);
                cmd.BindIndexBuffer(_indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
                shared = true;
            }
            var (first, n, offset) = _meshes[item.Mesh];
            cmd.DrawIndexed(n, 1, first, offset, i);
        }
        if (!shared)
        {
            cmd.BindVertexBuffers(0, [_vertices.Buffer], [new ByteSize64(0)]);
            cmd.BindIndexBuffer(_indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
        }
        if (frame.Beams.Count > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _beamPipeline);
            for (var i = count; i < all; i++)
            {
                var (first, n, offset) = _meshes[frame.Beams[i - count].Mesh];
                cmd.DrawIndexed(n, 1, first, offset, i);
            }
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
    /// A model's mesh on the GPU, uploaded now if it's new; null for one with no triangles.
    /// </summary>
    (GpuBuffer Vertices, GpuBuffer Indices, int Count)? ModelMesh(MeshData mesh)
    {
        if (_models.TryGetValue(mesh, out var m))
            return m;
        if (mesh.Indices.Count == 0)
            return null;
        var vertices = new MeshVertex[mesh.Positions.Count];
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = new MeshVertex { Position = mesh.Positions[i], Normal = mesh.Normals[i] };
        m = (Upload(vertices, VkBufferUsageFlags.VertexBufferBit), Upload<uint>([.. mesh.Indices], VkBufferUsageFlags.IndexBufferBit), mesh.Indices.Count);
        _models[mesh] = m;
        return m;
    }

    /// <summary>
    /// A bigger host-visible storage buffer (at least <paramref name="minimum"/> items) in place of
    /// <paramref name="old"/>, which this frame slot's finished with, pointed to by its set's
    /// <paramref name="binding"/>.
    /// </summary>
    (GpuBuffer Buffer, int Capacity) Grow<T>(GpuBuffer? old, int needed, int minimum, VkDescriptorSet set, int binding)
        where T : unmanaged
    {
        old?.Dispose();
        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, minimum));
        var buffer = GpuBuffer.Create(
            _gpu.Allocator,
            ByteSize.Of<T>() * capacity,
            VkBufferUsageFlags.StorageBufferBit,
            VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit
        );
        var info = new VkDescriptorBufferInfo { Buffer = buffer.Buffer, Range = ByteSize.Of<T>() * capacity };
        _device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet
                {
                    DstSet = set,
                    DstBinding = binding,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageBuffer,
                    BufferInfo = &info,
                },
            ],
            []
        );
        return (buffer, capacity);
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
        {
            f.Buffer?.Dispose();
            f.Lights?.Dispose();
        }
        _color?.Dispose();
        _depth?.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        foreach (var (v, i, _) in _models.Values)
        {
            v.Dispose();
            i.Dispose();
        }
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipeline(_beamPipeline, null);
        _gobos.Dispose();
        _device.DestroyPipelineLayout(_pipelineLayout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
