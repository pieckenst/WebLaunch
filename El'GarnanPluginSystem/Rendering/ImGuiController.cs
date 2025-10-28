using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using ImGuiNET;
using Veldrid;


namespace El_Garnan_Plugin_Loader.Rendering
{
    public class ImGuiController : IDisposable
    {
        private static readonly Assembly ImGuiResourceAssembly = typeof(ImGuiRenderer).GetTypeInfo().Assembly;
        private GraphicsDevice _gd;
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private DeviceBuffer _projMatrixBuffer;
        private Texture _fontTexture;
        private TextureView _fontTextureView;
        private Shader _vertexShader;
        private Shader _fragmentShader;
        private ResourceLayout _layout;
        private ResourceLayout _textureLayout;
        private Pipeline _pipeline;
        private ResourceSet _mainResourceSet;
        private ResourceSet _fontTextureResourceSet;
        private IntPtr _fontAtlasID = (IntPtr)1;
        private bool _frameBegun;
        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        public ImGuiController(GraphicsDevice gd, OutputDescription outputDescription, int width, int height)
        {
            _gd = gd;
            _windowWidth = width;
            _windowHeight = height;

            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.AddFontDefault();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            
            CreateDeviceResources(gd, outputDescription);
            SetKeyMappings();

            SetPerFrameImGuiData(1f / 60f);
            ImGui.NewFrame();
            _frameBegun = true;
        }
        private void SetKeyMappings()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

            // Add key mappings using modern ImGui API
            io.AddKeyEvent(ImGuiKey.Tab, false);
            io.AddKeyEvent(ImGuiKey.LeftArrow, false);
            io.AddKeyEvent(ImGuiKey.RightArrow, false);
            io.AddKeyEvent(ImGuiKey.UpArrow, false);
            io.AddKeyEvent(ImGuiKey.DownArrow, false);
            io.AddKeyEvent(ImGuiKey.PageUp, false);
            io.AddKeyEvent(ImGuiKey.PageDown, false);
            io.AddKeyEvent(ImGuiKey.Home, false);
            io.AddKeyEvent(ImGuiKey.End, false);
            io.AddKeyEvent(ImGuiKey.Delete, false);
            io.AddKeyEvent(ImGuiKey.Backspace, false);
            io.AddKeyEvent(ImGuiKey.Enter, false);
            io.AddKeyEvent(ImGuiKey.Escape, false);
            io.AddKeyEvent(ImGuiKey.Space, false);
            io.AddKeyEvent(ImGuiKey.A, false);
            io.AddKeyEvent(ImGuiKey.C, false);
            io.AddKeyEvent(ImGuiKey.V, false);
            io.AddKeyEvent(ImGuiKey.X, false);
            io.AddKeyEvent(ImGuiKey.Y, false);
            io.AddKeyEvent(ImGuiKey.Z, false);
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }
        private void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription)
        {
            ResourceFactory factory = gd.ResourceFactory;
            _vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            _projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            
            byte[] vertexShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex);
            byte[] fragmentShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment);
            string vertexEntry = gd.BackendType == GraphicsBackend.Vulkan ? "main" : "VS";
            string fragmentEntry = gd.BackendType == GraphicsBackend.Vulkan ? "main" : "FS";
            _vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, vertexEntry));
            _fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, fragmentEntry));

            VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("UV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
            };

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

            GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, true),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, new[] { _vertexShader, _fragmentShader }),
                new ResourceLayout[] { _layout, _textureLayout },
                outputDescription,
                ResourceBindingModel.Default);
            _pipeline = factory.CreateGraphicsPipeline(ref pd);

            _mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_layout,
                _projMatrixBuffer,
                gd.PointSampler));

            RecreateFontDeviceTexture(gd);
        }

        private static byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages stage)
        {
            string resourceSuffix = GetResourceSuffix(factory.BackendType, name, stage);
            var resourceName = ImGuiResourceAssembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                throw new InvalidOperationException($"Unable to locate ImGui shader resource '{resourceSuffix}' in assembly '{ImGuiResourceAssembly.FullName}'.");
            }

            using var stream = ImGuiResourceAssembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Failed to open shader resource stream '{resourceName}'.");

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string GetResourceSuffix(GraphicsBackend backend, string name, ShaderStages stage)
        {
            return backend switch
            {
                GraphicsBackend.Direct3D11 => $"{name}.hlsl.bytes",
                GraphicsBackend.OpenGL => $"{name}.glsl",
                GraphicsBackend.OpenGLES => $"{name}.glsles",
                GraphicsBackend.Vulkan => $"{name}.spv",
                GraphicsBackend.Metal => $"{name}.metallib",
                _ => throw new NotSupportedException($"Unsupported graphics backend: {backend}")
            };
        }

        private void RecreateFontDeviceTexture(GraphicsDevice gd)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

            _fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width,
                (uint)height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));
            _fontTexture.Name = "ImGui.NET Font Texture";
            gd.UpdateTexture(
                _fontTexture,
                pixels,
                (uint)(bytesPerPixel * width * height),
                0,
                0,
                0,
                (uint)width,
                (uint)height,
                1,
                0,
                0);
            _fontTextureView = gd.ResourceFactory.CreateTextureView(_fontTexture);

            io.Fonts.SetTexID(_fontAtlasID);

            _fontTextureResourceSet = gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView));
        }

        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds;
        }

        public void Render(GraphicsDevice gd, CommandList cl)
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData(), gd, cl);
            }
        }

        private void RenderImDrawData(ImDrawDataPtr drawData, GraphicsDevice gd, CommandList cl)
{
    uint vertexOffsetInVertices = 0;
    uint indexOffsetInElements = 0;

    if (drawData.CmdListsCount == 0) return;

    uint totalVBSize = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
    if (totalVBSize > _vertexBuffer.SizeInBytes)
    {
        gd.DisposeWhenIdle(_vertexBuffer);
        _vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
    }

    uint totalIBSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));
    if (totalIBSize > _indexBuffer.SizeInBytes)
    {
        gd.DisposeWhenIdle(_indexBuffer);
        _indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
    }

    for (int i = 0; i < drawData.CmdListsCount; i++)
    {
        ImDrawListPtr cmdList = drawData.CmdLists[i];

        cl.UpdateBuffer(
            _vertexBuffer,
            vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
            cmdList.VtxBuffer.Data,
            (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));

        cl.UpdateBuffer(
            _indexBuffer,
            indexOffsetInElements * sizeof(ushort),
            cmdList.IdxBuffer.Data,
            (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));

        vertexOffsetInVertices += (uint)cmdList.VtxBuffer.Size;
        indexOffsetInElements += (uint)cmdList.IdxBuffer.Size;
    }

    ImGuiIOPtr io = ImGui.GetIO();
    Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
        0f,
        io.DisplaySize.X,
        io.DisplaySize.Y,
        0.0f,
        -1.0f,
        1.0f);

    cl.UpdateBuffer(_projMatrixBuffer, 0, ref mvp);
    cl.SetVertexBuffer(0, _vertexBuffer);
    cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
    cl.SetPipeline(_pipeline);
    cl.SetGraphicsResourceSet(0, _mainResourceSet);

    drawData.ScaleClipRects(io.DisplayFramebufferScale);

    int vtx_offset = 0;
    int idx_offset = 0;
    for (int n = 0; n < drawData.CmdListsCount; n++)
    {
        ImDrawListPtr cmdList = drawData.CmdLists[n];
        for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
        {
            ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmd_i];
            if (pcmd.UserCallback != IntPtr.Zero)
            {
                throw new NotImplementedException();
            }
            else
            {
                if (pcmd.TextureId != IntPtr.Zero)
                {
                    if (pcmd.TextureId == _fontAtlasID)
                    {
                        cl.SetGraphicsResourceSet(1, _fontTextureResourceSet);
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }

                cl.SetScissorRect(
                    0,
                    (uint)pcmd.ClipRect.X,
                    (uint)pcmd.ClipRect.Y,
                    (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                    (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                cl.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)idx_offset, (int)pcmd.VtxOffset + vtx_offset, 0);
            }
        }
        vtx_offset += cmdList.VtxBuffer.Size;
        idx_offset += cmdList.IdxBuffer.Size;
    }
}

        public void Update(float deltaSeconds, InputSnapshot snapshot)
        {
            if (!_frameBegun)
            {
                SetPerFrameImGuiData(deltaSeconds);
                UpdateImGuiInput(snapshot);
                ImGui.NewFrame();
                _frameBegun = true;
            }
        }

        private void UpdateImGuiInput(InputSnapshot snapshot)
{
    ImGuiIOPtr io = ImGui.GetIO();

    // Modern input handling
    io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
    io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
    io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
    io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
    io.AddMouseWheelEvent(0, snapshot.WheelDelta);

    foreach (var c in snapshot.KeyCharPresses)
    {
        io.AddInputCharacter(c);
    }

    foreach (var keyEvent in snapshot.KeyEvents)
    {
        io.AddKeyEvent(GetImGuiKey(keyEvent.Key), keyEvent.Down);
        io.AddKeyEvent(ImGuiKey.ModCtrl, keyEvent.Modifiers.HasFlag(ModifierKeys.Control));
        io.AddKeyEvent(ImGuiKey.ModShift, keyEvent.Modifiers.HasFlag(ModifierKeys.Shift));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyEvent.Modifiers.HasFlag(ModifierKeys.Alt));
    }
}

private static ImGuiKey GetImGuiKey(Key key)
{
    switch (key)
    {
        case Key.Tab: return ImGuiKey.Tab;
        case Key.Left: return ImGuiKey.LeftArrow;
        case Key.Right: return ImGuiKey.RightArrow;
        case Key.Up: return ImGuiKey.UpArrow;
        case Key.Down: return ImGuiKey.DownArrow;
        // Add more key mappings as needed
        default: return ImGuiKey.None;
    }
}

        public void Dispose()
        {
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _projMatrixBuffer?.Dispose();
            _fontTexture?.Dispose();
            _fontTextureView?.Dispose();
            _vertexShader?.Dispose();
            _fragmentShader?.Dispose();
            _layout?.Dispose();
            _textureLayout?.Dispose();
            _pipeline?.Dispose();
            _mainResourceSet?.Dispose();
            _fontTextureResourceSet?.Dispose();
        }
    }
}