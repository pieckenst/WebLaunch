using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using ImGuiNET;

using Veldrid;
using Veldrid.Sdl2;

// Veldrid objects are setup in method called by constructor
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace El_Garnan_Plugin_Loader.Rendering
{
    /// <summary>
    /// A modified version of Veldrid.ImGui's ImGuiRenderer.
    /// Manages input for ImGui and handles rendering ImGui's DrawLists with Veldrid.
    /// </summary>
    public class ImGuiBindings : IDisposable
    {
    private GraphicsDevice gd;
    private bool frameBegun;

    // Veldrid objects
    private DeviceBuffer vertexBuffer;
    private DeviceBuffer indexBuffer;
    private DeviceBuffer projMatrixBuffer;
    private Texture fontTexture;
    private TextureView fontTextureView;
    private Shader vertexShader;
    private Shader fragmentShader;
    private ResourceLayout layout;
    private ResourceLayout textureLayout;
    private Pipeline pipeline;
    private ResourceSet mainResourceSet;
    private ResourceSet fontTextureResourceSet;

    private IntPtr fontAtlasID = (IntPtr)1;
    private bool controlDown;
    private bool shiftDown;
    private bool altDown;
    private bool winKeyDown;

    private IntPtr iniPathPtr;

    private int windowWidth;
    private int windowHeight;
    private Vector2 scaleFactor = Vector2.One;

    // Image trackers
    private readonly Dictionary<TextureView, ResourceSetInfo> setsByView
        = new Dictionary<TextureView, ResourceSetInfo>();

    private readonly Dictionary<Texture, TextureView> autoViewsByTexture
        = new Dictionary<Texture, TextureView>();

    private readonly Dictionary<IntPtr, ResourceSetInfo> viewsById = new Dictionary<IntPtr, ResourceSetInfo>();
    private readonly List<IDisposable> ownedResources = new List<IDisposable>();
    private int lastAssignedID = 100;

    private const float DefaultFontPxSize = 18f;

    private delegate void SetClipboardTextDelegate(IntPtr userData, string text);

    private delegate string GetClipboardTextDelegate();

    // variables because they need to exist for the program duration without being gc'd
    private SetClipboardTextDelegate setText;
    private GetClipboardTextDelegate getText;

    /// <summary>
    /// Constructs a new ImGui renderer using default ini location and font size.
    /// </summary>
    public ImGuiBindings(GraphicsDevice gd, OutputDescription outputDescription, int width, int height)
        : this(gd, outputDescription, width, height, CreateDefaultIniFile(), DefaultFontPxSize)
    {
    }

    /// <summary>
    /// Constructs a new ImGui renderer with custom ini location and font size.
    /// </summary>
    public ImGuiBindings(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, FileInfo iniPath, float fontPxSize)
    {
        this.gd = gd;
        windowWidth = width;
        windowHeight = height;

        IntPtr context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;

        SetIniPath(iniPath.FullName);

        setText = new SetClipboardTextDelegate(SetClipboardText);
        getText = new GetClipboardTextDelegate(GetClipboardText);

        io.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(setText);
        io.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(getText);
        io.ClipboardUserData = IntPtr.Zero;

        CreateDeviceResources(gd, outputDescription, fontPxSize);
        SetKeyMappings();

        SetPerFrameImGuiData(1f / 60f);

        ImGui.NewFrame();
        frameBegun = true;
    }

    private static FileInfo CreateDefaultIniFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "imgui.ini");
        return new FileInfo(path);
    }

    private void SetIniPath(string iniPath)
    {
        if (iniPathPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(iniPathPtr);
        }

        iniPathPtr = Marshal.StringToHGlobalAnsi(iniPath);

        unsafe
        {
            ImGui.GetIO().NativePtr->IniFilename = (byte*)iniPathPtr.ToPointer();
        }
    }

    private static void SetClipboardText(IntPtr userData, string text)
    {
        // text always seems to have an extra newline, but I'll leave it for now
        Sdl2Native.SDL_SetClipboardText(text);
    }

    private static string GetClipboardText()
    {
        return Sdl2Native.SDL_GetClipboardText();
    }

    public void WindowResized(int width, int height)
    {
        windowWidth = width;
        windowHeight = height;
    }

    public void DestroyDeviceObjects()
    {
        Dispose();
    }

    public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription, float fontPxSize)
    {
        this.gd = gd;
        ResourceFactory factory = gd.ResourceFactory;
        vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        vertexBuffer.Name = "ImGui.NET Vertex Buffer";
        indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
        indexBuffer.Name = "ImGui.NET Index Buffer";

        var fontManager = new FontManager();
        fontManager.SetupFonts(fontPxSize);
        RecreateFontDeviceTexture(gd);

        projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

        byte[] vertexShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex);
        byte[] fragmentShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment);
        string vertexEntry = gd.BackendType == GraphicsBackend.Metal ? "VS" : "main";
        string fragmentEntry = gd.BackendType == GraphicsBackend.Metal ? "FS" : "main";
        vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, vertexEntry));
        fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, fragmentEntry));

        VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
        {
            new VertexLayoutDescription(
                new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
        };

        layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

        GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription(false, false, ComparisonKind.Always),
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription(vertexLayouts, new[] { vertexShader, fragmentShader }),
            new ResourceLayout[] { layout, textureLayout },
            outputDescription,
            ResourceBindingModel.Default);
        pipeline = factory.CreateGraphicsPipeline(ref pd);

        mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(layout,
            projMatrixBuffer,
            gd.PointSampler));

        fontTextureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(textureLayout, fontTextureView));
    }

    /// <summary>
    /// Gets or creates a handle for a texture to be drawn with ImGui.
    /// Pass the returned handle to Image() or ImageButton().
    /// </summary>
    public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
    {
        if (!setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
        {
            ResourceSet resourceSet = factory.CreateResourceSet(new ResourceSetDescription(textureLayout, textureView));
            rsi = new ResourceSetInfo(GetNextImGuiBindingID(), resourceSet);

            setsByView.Add(textureView, rsi);
            viewsById.Add(rsi.ImGuiBinding, rsi);
            ownedResources.Add(resourceSet);
        }

        return rsi.ImGuiBinding;
    }

    private IntPtr GetNextImGuiBindingID()
    {
        int newID = lastAssignedID++;
        return (IntPtr)newID;
    }

    /// <summary>
    /// Gets or creates a handle for a texture to be drawn with ImGui.
    /// Pass the returned handle to Image() or ImageButton().
    /// </summary>
    public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
    {
        if (!autoViewsByTexture.TryGetValue(texture, out var textureView))
        {
            textureView = factory.CreateTextureView(texture);
            autoViewsByTexture.Add(texture, textureView);
            ownedResources.Add(textureView);
        }

        return GetOrCreateImGuiBinding(factory, textureView);
    }

    /// <summary>
    /// Retrieves the shader texture binding for the given helper handle.
    /// </summary>
    public ResourceSet GetImageResourceSet(IntPtr imGuiBinding)
    {
        if (!viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo tvi))
        {
            throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
        }

        return tvi.ResourceSet;
    }

    public void ClearCachedImageResources()
    {
        foreach (IDisposable resource in ownedResources)
        {
            resource.Dispose();
        }

        ownedResources.Clear();
        setsByView.Clear();
        viewsById.Clear();
        autoViewsByTexture.Clear();
        lastAssignedID = 100;
    }

    private byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages stage)
    {
        switch (factory.BackendType)
        {
            case GraphicsBackend.Direct3D11:
            {
                string resourceName = name + ".hlsl.bytes";
                return ImGuiResourceLoader.LoadBytes("Shaders", "HLSL", resourceName);
            }

            case GraphicsBackend.OpenGL:
            {
                string resourceName = name + ".glsl";
                return ImGuiResourceLoader.LoadBytes("Shaders", "GLSL", resourceName);
            }

            case GraphicsBackend.Vulkan:
            {
                string resourceName = name + ".spv";
                return ImGuiResourceLoader.LoadBytes("Shaders", "SPIR-V", resourceName);
            }

            case GraphicsBackend.Metal:
            {
                string resourceName = name + ".metallib";
                return ImGuiResourceLoader.LoadBytes("Shaders", "Metal", resourceName);
            }

            default:
                throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Recreates the device texture used to render text.
    /// </summary>
    public void RecreateFontDeviceTexture(GraphicsDevice gd)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        // Build
        IntPtr pixels;
        int width, height, bytesPerPixel;
        io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bytesPerPixel);
        // Store our identifier
        io.Fonts.SetTexID(fontAtlasID);

        fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
            (uint)width,
            (uint)height,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled));
        fontTexture.Name = "ImGui.NET Font Texture";
        gd.UpdateTexture(
            fontTexture,
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
        fontTextureView = gd.ResourceFactory.CreateTextureView(fontTexture);

        io.Fonts.ClearTexData();
    }

    /// <summary>
    /// Renders the ImGui draw list data.
    /// This method requires a <see cref="GraphicsDevice"/> because it may create new DeviceBuffers if the size of vertex
    /// or index data has increased beyond the capacity of the existing buffers.
    /// A <see cref="CommandList"/> is needed to submit drawing and resource update commands.
    /// </summary>
    public void Render(GraphicsDevice gd, CommandList cl)
    {
        if (frameBegun)
        {
            frameBegun = false;
            ImGui.Render();
            RenderImDrawData(ImGui.GetDrawData(), gd, cl);
        }
    }

    /// <summary>
    /// Updates ImGui input and IO configuration state.
    /// </summary>
    public void Update(float deltaSeconds, InputSnapshot snapshot)
    {
        if (frameBegun)
        {
            ImGui.Render();
        }

        SetPerFrameImGuiData(deltaSeconds);
        UpdateImGuiInput(snapshot);

        frameBegun = true;
        ImGui.NewFrame();
    }

    /// <summary>
    /// Sets per-frame data based on the associated window.
    /// This is called by Update(float).
    /// </summary>
    private void SetPerFrameImGuiData(float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(
            windowWidth / scaleFactor.X,
            windowHeight / scaleFactor.Y);
        io.DisplayFramebufferScale = scaleFactor;
        io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
    }

    public ResourceSet GetFontTextureResourceSet()
    {
        return fontTextureResourceSet;
    }

    private void UpdateImGuiInput(InputSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        Vector2 mousePosition = snapshot.MousePosition;

        // Determine if any of the mouse buttons were pressed during this snapshot period, even if they are no longer held.
        bool leftPressed = false;
        bool middlePressed = false;
        bool rightPressed = false;

        foreach (MouseEvent me in snapshot.MouseEvents)
        {
            if (me.Down)
            {
                switch (me.MouseButton)
                {
                    case MouseButton.Left:
                        leftPressed = true;
                        break;

                    case MouseButton.Middle:
                        middlePressed = true;
                        break;

                    case MouseButton.Right:
                        rightPressed = true;
                        break;
                }
            }
        }

        io.MouseDown[0] = leftPressed || snapshot.IsMouseDown(MouseButton.Left);
        io.MouseDown[1] = rightPressed || snapshot.IsMouseDown(MouseButton.Right);
        io.MouseDown[2] = middlePressed || snapshot.IsMouseDown(MouseButton.Middle);
        io.MousePos = mousePosition;
        io.MouseWheel = snapshot.WheelDelta;

        IReadOnlyList<char> keyCharPresses = snapshot.KeyCharPresses;

        for (int i = 0; i < keyCharPresses.Count; i++)
        {
            char c = keyCharPresses[i];
            io.AddInputCharacter(c);
        }

        IReadOnlyList<KeyEvent> keyEvents = snapshot.KeyEvents;

        for (int i = 0; i < keyEvents.Count; i++)
        {
            KeyEvent keyEvent = keyEvents[i];
            ImGuiKey? mappedKey = ConvertKey(keyEvent.Key);

            if (mappedKey.HasValue)
            {
                io.AddKeyEvent(mappedKey.Value, keyEvent.Down);
            }

            switch (keyEvent.Key)
            {
                case Key.ControlLeft:
                case Key.ControlRight:
                    controlDown = keyEvent.Down;
                    break;
                case Key.ShiftLeft:
                case Key.ShiftRight:
                    shiftDown = keyEvent.Down;
                    break;
                case Key.AltLeft:
                case Key.AltRight:
                    altDown = keyEvent.Down;
                    break;
                case Key.WinLeft:
                case Key.WinRight:
                    winKeyDown = keyEvent.Down;
                    break;
            }
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, controlDown);
        io.AddKeyEvent(ImGuiKey.ModShift, shiftDown);
        io.AddKeyEvent(ImGuiKey.ModAlt, altDown);
        io.AddKeyEvent(ImGuiKey.ModSuper, winKeyDown);

        io.KeyCtrl = controlDown;
        io.KeyAlt = altDown;
        io.KeyShift = shiftDown;
        io.KeySuper = winKeyDown;
    }

    private static ImGuiKey? ConvertKey(Key key)
    {
        return key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Delete => ImGuiKey.Delete,
            Key.BackSpace => ImGuiKey.Backspace,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Space => ImGuiKey.Space,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.A => ImGuiKey.A,
            Key.C => ImGuiKey.C,
            Key.V => ImGuiKey.V,
            Key.X => ImGuiKey.X,
            Key.Y => ImGuiKey.Y,
            Key.Z => ImGuiKey.Z,
            Key.B => ImGuiKey.B,
            Key.D => ImGuiKey.D,
            Key.E => ImGuiKey.E,
            Key.F => ImGuiKey.F,
            Key.G => ImGuiKey.G,
            Key.H => ImGuiKey.H,
            Key.I => ImGuiKey.I,
            Key.J => ImGuiKey.J,
            Key.K => ImGuiKey.K,
            Key.L => ImGuiKey.L,
            Key.M => ImGuiKey.M,
            Key.N => ImGuiKey.N,
            Key.O => ImGuiKey.O,
            Key.P => ImGuiKey.P,
            Key.Q => ImGuiKey.Q,
            Key.R => ImGuiKey.R,
            Key.S => ImGuiKey.S,
            Key.T => ImGuiKey.T,
            Key.U => ImGuiKey.U,
            Key.W => ImGuiKey.W,
            Key.F1 => ImGuiKey.F1,
            Key.F2 => ImGuiKey.F2,
            Key.F3 => ImGuiKey.F3,
            Key.F4 => ImGuiKey.F4,
            Key.F5 => ImGuiKey.F5,
            Key.F6 => ImGuiKey.F6,
            Key.F7 => ImGuiKey.F7,
            Key.F8 => ImGuiKey.F8,
            Key.F9 => ImGuiKey.F9,
            Key.F10 => ImGuiKey.F10,
            Key.F11 => ImGuiKey.F11,
            Key.F12 => ImGuiKey.F12,
            _ => null,
        };
    }

    private static void SetKeyMappings()
    {
        ImGuiIOPtr io = ImGui.GetIO();
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
        io.AddKeyEvent(ImGuiKey.KeypadEnter, false);
        io.AddKeyEvent(ImGuiKey.A, false);
        io.AddKeyEvent(ImGuiKey.C, false);
        io.AddKeyEvent(ImGuiKey.V, false);
        io.AddKeyEvent(ImGuiKey.X, false);
        io.AddKeyEvent(ImGuiKey.Y, false);
        io.AddKeyEvent(ImGuiKey.Z, false);
    }

    private void RenderImDrawData(ImDrawDataPtr drawData, GraphicsDevice gd, CommandList cl)
    {
        uint vertexOffsetInVertices = 0;
        uint indexOffsetInElements = 0;

        if (drawData.CmdListsCount == 0)
        {
            return;
        }

        uint totalVbSize = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());

        if (totalVbSize > vertexBuffer.SizeInBytes)
        {
            gd.DisposeWhenIdle(vertexBuffer);
            vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVbSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        }

        uint totalIbSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));

        if (totalIbSize > indexBuffer.SizeInBytes)
        {
            gd.DisposeWhenIdle(indexBuffer);
            indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIbSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
        }

        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[i];
            
            cl.UpdateBuffer(
                vertexBuffer,
                vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
                cmdList.VtxBuffer.Data,
                (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));

            cl.UpdateBuffer(
                indexBuffer,
                indexOffsetInElements * sizeof(ushort),
                cmdList.IdxBuffer.Data,
                (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));

            vertexOffsetInVertices += (uint)cmdList.VtxBuffer.Size;
            indexOffsetInElements += (uint)cmdList.IdxBuffer.Size;
        }

        // Setup orthographic projection matrix into our constant buffer
        ImGuiIOPtr io = ImGui.GetIO();
        Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
            0f,
            io.DisplaySize.X,
            io.DisplaySize.Y,
            0.0f,
            -1.0f,
            1.0f);

        this.gd.UpdateBuffer(this.projMatrixBuffer, 0, ref mvp);

        cl.SetVertexBuffer(0, vertexBuffer);
        cl.SetIndexBuffer(indexBuffer, IndexFormat.UInt16);
        cl.SetPipeline(pipeline);
        cl.SetGraphicsResourceSet(0, mainResourceSet);

        drawData.ScaleClipRects(io.DisplayFramebufferScale);

        // Render command lists
        int globalIdxOffset = 0;
        int globalVtxOffset = 0;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];

            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdI];

                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    throw new NotImplementedException();
                }
                else
                {
                    if (pcmd.TextureId != IntPtr.Zero)
                    {
                        if (pcmd.TextureId == fontAtlasID)
                        {
                            cl.SetGraphicsResourceSet(1, fontTextureResourceSet);
                        }
                        else
                        {
                            cl.SetGraphicsResourceSet(1, GetImageResourceSet(pcmd.TextureId));
                        }
                    }

                    cl.SetScissorRect(
                        0,
                        (uint)pcmd.ClipRect.X,
                        (uint)pcmd.ClipRect.Y,
                        (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                        (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                    cl.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)globalIdxOffset, (int)pcmd.VtxOffset + globalVtxOffset, 0);
                }
            }

            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    /// <summary>
    /// Frees all graphics resources used by the renderer.
    /// </summary>
    public void Dispose()
    {
        vertexBuffer.Dispose();
        indexBuffer.Dispose();
        projMatrixBuffer.Dispose();
        fontTexture.Dispose();
        fontTextureView.Dispose();
        vertexShader.Dispose();
        fragmentShader.Dispose();
        layout.Dispose();
        textureLayout.Dispose();
        pipeline.Dispose();
        mainResourceSet.Dispose();

        foreach (IDisposable resource in ownedResources)
        {
            resource.Dispose();
        }
    }

    private struct ResourceSetInfo
    {
        public readonly IntPtr ImGuiBinding;
        public readonly ResourceSet ResourceSet;

        public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
        {
            ImGuiBinding = imGuiBinding;
            ResourceSet = resourceSet;
        }
    }
} }
