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
            VeldridStartup.CreateWindowAndGraphicsDevice(
                new WindowCreateInfo(50, 50, 1280, 720, WindowState.Normal, "Plugin Interface"),
                new GraphicsDeviceOptions(true, null, true, ResourceBindingModel.Improved, true, true),
                out _window,
                out _gd);

            _window.Resized += () =>
            {
                _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
                _imgui.WindowResized(_window.Width, _window.Height);
            };

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