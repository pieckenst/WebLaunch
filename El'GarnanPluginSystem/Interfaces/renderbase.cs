// Файл: ElGarnanPluginSystem/Interfaces/renderbase.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics; // *** ФИКС: Добавлена директива using для Vector2 и Vector4 ***
using El_Garnan_Plugin_Loader.Models;
using El_Garnan_Plugin_Loader.Rendering;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

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
        // Убираем таймер
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

        public unsafe void Initialize()
        {
            try
            {
                // *** ФИКС CS1503: Явно приводим int к нужному enum-типу SDLInitFlags ***
                if (Sdl2Native.SDL_Init((SDLInitFlags)0x00000020) != 0) // 0x20 это SDL_INIT_VIDEO
                {
                    _logger.Error($"Failed to initialize SDL");
                    return;
                }

                int width = 1024;
                int height = 768;

                // *** ФИКС CS0306: Создаем экземпляр структуры, а не указатель на нее ***
                SDL_DisplayMode mode;
                if (Sdl2Native.SDL_GetCurrentDisplayMode(0, &mode) != 0)
                {
                    _logger.Warning("Could not get display mode. Using default position.");
                    // Устанавливаем значения по умолчанию, если не удалось получить реальные
                    mode.w = 1920;
                    mode.h = 1080;
                }

                int x = (mode.w - width) / 2;
                int y = (mode.h - height) / 2;

                // *** ФИКС CS1503: Явно приводим uint к нужному enum-типу SDL_WindowFlags ***
                var windowFlags = (SDL_WindowFlags)0x00000010 | (SDL_WindowFlags)0x00000008; // BORDERLESS | HIDDEN

                // Sdl2Native.SDL_CreateWindow ожидает uint, поэтому здесь приведение не нужно, если windowFlags уже uint.
                // Но для ясности, лучше сразу объявить его как SDL_WindowFlags.
                var sdlWindowHandle = Sdl2Native.SDL_CreateWindow(
                    "Plugin Interface", x, y, width, height,
                    windowFlags
                );

                // *** ФИКС CS1503: Передаем в конструктор правильный enum-тип SDL_WindowFlags ***
                _window = new Sdl2Window("Plugin Interface", x, y, width, height, windowFlags, false);

                var options = new GraphicsDeviceOptions(true, null, true, ResourceBindingModel.Improved, true, true);

                _gd = VeldridStartup.CreateGraphicsDevice(_window, options);

                _cl = _gd.ResourceFactory.CreateCommandList();
                _imgui = new ImGuiBindings(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height);

                _isInitialized = true;
                _logger.Information("ImGui renderer initialized successfully");

                _window.Visible = true;
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

        // ВАШ МЕТОД RENDER() УЖЕ ПРАВИЛЬНЫЙ, ОСТАВЛЯЕМ ЕГО КАК ЕСТЬ
        public void Render()
        {
            if (!_isInitialized) return;

            try
            {
                while (_window.Exists)
                {
                    var snapshot = _window.PumpEvents();
                    if (!_window.Exists) break;

                    _imgui.Update(1f / 60f, snapshot);
                    ImGui.NewFrame();

                    // Отрисовка фона напрямую (этот вариант для случая без дочернего окна)
                    var drawList = ImGui.GetBackgroundDrawList();
                    // Используем Vector4 из System.Numerics
                    var bgColor = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.08f, 0.08f, 0.11f, 1.0f));
                    drawList.AddRectFilled(System.Numerics.Vector2.Zero, ImGui.GetIO().DisplaySize, bgColor);

                    if (_plugins != null)
                    {
                        foreach (var plugin in _plugins.Where(p => p.SupportsImGui))
                        {
                            plugin.RenderImGui();
                        }
                    }

                    ImGui.Render();
                    _cl.Begin();
                    _cl.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
                    _cl.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 1));
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
            finally
            {
                _logger.Information("Render window closed. Shutting down application.");
                Environment.Exit(0);
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