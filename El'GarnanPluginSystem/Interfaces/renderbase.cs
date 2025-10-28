using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using System.Numerics;
using El_Garnan_Plugin_Loader.Models;
using El_Garnan_Plugin_Loader.Rendering;

namespace El_Garnan_Plugin_Loader.Interfaces
{
    public interface IPluginRenderer : IDisposable
{
    void Initialize();
    void Render();
    bool IsInitialized { get; }
    void SetPlugins(IEnumerable<IGamePlugin> plugins);
}

public class ImGuiPluginRenderer : IPluginRenderer
{
    private System.Timers.Timer _visibilityTimer;
    private ImGuiBindings _imgui;

    private GraphicsDevice _gd;
    private CommandList _cl;
    private Sdl2Window _window;
    private IEnumerable<IGamePlugin> _plugins;
    private bool _isInitialized;
    private readonly ILogger _logger;

    public bool IsInitialized => _isInitialized;

    public ImGuiPluginRenderer(ILogger logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        try
        {
           var windowCreateInfo = new WindowCreateInfo(
    0, 0,  // Position will be set by ImGui
    400, 300,  // Initial size
    WindowState.Normal,
    "Plugin Interface")
{
    WindowInitialState = WindowState.Normal
    // Removed WindowInitialBorder as it doesn't exist
};

VeldridStartup.CreateWindowAndGraphicsDevice(
    windowCreateInfo,
    new GraphicsDeviceOptions(true, null, true, ResourceBindingModel.Improved, true, true),
    out _window,
    out _gd);

// Set SDL window flags after creation
if (_window is Sdl2Window sdlWindow)
{
    var sdl2Window = sdlWindow.SdlWindowHandle;
    Sdl2Native.SDL_SetWindowBordered(sdl2Window, 0);  // Remove border
    Sdl2Native.SDL_SetWindowResizable(sdl2Window, 0); // Disable resizing
    
    // Get display bounds using Veldrid's SDL2 bindings
    var displayIndex = Sdl2Native.SDL_GetWindowDisplayIndex(sdl2Window);
    int displayWidth = 0;
    int displayHeight = 0;
    unsafe
    {
        SDL_DisplayMode mode;
        if (Sdl2Native.SDL_GetCurrentDisplayMode(displayIndex, &mode) == 0)
        {
            displayWidth = mode.w;
            displayHeight = mode.h;
        }
    }
    
    int windowWidth = 1024;  // Match this with your ImGui window size
    int windowHeight = 768;
    int windowX = (displayWidth - windowWidth) / 2;
    int windowY = (displayHeight - windowHeight) / 2;
    
    Sdl2Native.SDL_SetWindowPosition(sdl2Window, windowX, windowY);
    Sdl2Native.SDL_SetWindowSize(sdl2Window, windowWidth, windowHeight);
}

// In the Initialize method, after creating the window
_window.Visible = false;  // Start hidden

// Set up a timer to make the window visible after a delay
_visibilityTimer = new System.Timers.Timer(5000); // 5 seconds
_visibilityTimer.Elapsed += (s, e) => 
{
    if (_window != null)
    {
        _window.Visible = true;
        _visibilityTimer.Stop();
        _visibilityTimer.Dispose();
    }
};
_visibilityTimer.AutoReset = false;
_visibilityTimer.Start();


            _cl = _gd.ResourceFactory.CreateCommandList();
            _imgui = new ImGuiBindings(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height);
            
            _isInitialized = true;
            _logger.Information("ImGui renderer initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize ImGui renderer", ex);
            throw;
        }
    }

    public void SetPlugins(IEnumerable<IGamePlugin> plugins)
    {
        _plugins = plugins;
    }

    public void Render()
{
    if (!_isInitialized || _plugins == null) 
    {
        _logger.Warning("Renderer not initialized or no plugins available");
        return;
    }

    try
    {
        var snapshot = _window.PumpEvents();
        if (!_window.Exists) 
        {
            _logger.Warning("Window does not exist");
            return;
        }

        // Begin frame
        _imgui.Update(1f/60f, snapshot);

        // Create a new ImGui frame
        ImGui.NewFrame();

        // Render each plugin's ImGui content
        bool hasContent = false;
        foreach (var plugin in _plugins.Where(p => p.SupportsImGui))
        {
            try 
            {
                plugin.RenderImGui();
                hasContent = true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error in plugin {plugin.Name} RenderImGui: {ex}");
            }
        }

        // Only render if we have content
        if (hasContent)
        {
            // End the ImGui frame
            ImGui.Render();

            // Render the frame
            _cl.Begin();
            _cl.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
            _cl.ClearColorTarget(0, new RgbaFloat(0.1f, 0.1f, 0.1f, 1f));
            
            _imgui.Render(_gd, _cl);

            _cl.End();
            _gd.SubmitCommands(_cl);
            _gd.SwapBuffers(_gd.MainSwapchain);
        }
    }
    catch (Exception ex)
    {
        _logger.Error($"Error in render loop: {ex}");
    }
}

    public void Dispose()
    {
        _imgui?.Dispose();

        _cl?.Dispose();
        _gd?.Dispose();
    }
}

}