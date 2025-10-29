using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;

namespace GamePlugins.FFXIV
{
    public class FFXIVGamePlugin : GamePluginBase
    {
        private dynamic _networkLogic;
        private readonly Dictionary<string, string> _config;
        private Assembly _coreSupportAssembly;
        private Assembly _imGuiAssembly;
        private Type _networkLogicType;
        private Type _imGuiType;
        private Type _imGuiWindowFlagsType;
        private Type _vector2Type;
        private Type _vector4Type;
        private Type _imGuiCondType;
        private MethodInfo _imGuiBeginMethod;
        private MethodInfo _imGuiEndMethod;
        private MethodInfo _imGuiTextMethod;
        private MethodInfo _imGuiSameLineMethod;
        private MethodInfo _imGuiSeparatorMethod;
        private MethodInfo _imGuiTextColoredMethod;
        private MethodInfo _setNextWindowPosMethod;

        private MethodInfo _setNextWindowSizeMethod;
        private MethodInfo _beginMethod;
        private MethodInfo _endMethod;
        private MethodInfo _textMethod;
        private MethodInfo _sameLineMethod;
        private MethodInfo _buttonMethod;
        // Add these fields at the class level

        private static Type _imGuiMouseButtonType;

        // These will hold the enum values
        private static object _noTitleBar;
        private static object _noResize;
        private static object _noMove;
        private static object _noCollapse;
        private static object _noSavedSettings;
        private static object _leftMouseButton;
        private object _imGuiDefaultWindowFlags;

        public override string PluginId => "ffxiv-launcher";
        public override string Name => "FFXIV Game Launcher";
        public override string Description => "Launches Final Fantasy XIV with authentication";
        public override string TargetApplication => "ffxiv_dx11.exe";
        public override Version Version => new Version(1, 0, 0);
        public override bool SupportsImGui => true;

        public override IReadOnlyCollection<PluginDependency> Dependencies => new[]
{
    new PluginDependency("CoreLibLaunchSupport", new Version(1, 0, 0)),
    new PluginDependency("LibDalamud", new Version(1, 0, 0)),
    new PluginDependency("ImGui.NET", new Version(1, 89, 4)),
    new PluginDependency("Veldrid", new Version(4, 9, 0))
};

        public FFXIVGamePlugin(ILogger logger) : base(logger)
        {
            _config = new Dictionary<string, string>();
        }


        private bool EnsureWindowFlagsInitialized()
        {
            Logger.Debug("Ensuring window flags are initialized...");

             var loadContext = new PluginLoadContext(AppContext.BaseDirectory);

            _imGuiAssembly = loadContext.LoadFromAssemblyName(new AssemblyName("ImGui.NET"))
                ?? throw new InvalidOperationException("Could not load ImGui.NET assembly");

            _imGuiWindowFlagsType = _imGuiAssembly.GetType("ImGuiNET.ImGuiWindowFlags")
                ?? throw new InvalidOperationException("Could not load ImGuiWindowFlags type");

            _imGuiMouseButtonType = _imGuiAssembly.GetType("ImGuiNET.ImGuiMouseButton")
                ?? throw new InvalidOperationException("Could not load ImGuiMouseButton type");

            if (_imGuiWindowFlagsType == null || _imGuiMouseButtonType == null)
            {
                Logger.Error("Required ImGui types are not initialized");
                return false;
            }

            try
            {
                // Initialize window flags if they're null
                if (_noTitleBar == null)
                {
                    Logger.Debug("Initializing NoTitleBar flag...");
                    _noTitleBar = Enum.ToObject(_imGuiWindowFlagsType, 1 << 0);
                }
                if (_noResize == null)
                {
                    Logger.Debug("Initializing NoResize flag...");
                    _noResize = Enum.ToObject(_imGuiWindowFlagsType, 1 << 1);
                }
                if (_noMove == null)
                {
                    Logger.Debug("Initializing NoMove flag...");
                    _noMove = Enum.ToObject(_imGuiWindowFlagsType, 1 << 2);
                }
                if (_noCollapse == null)
                {
                    Logger.Debug("Initializing NoCollapse flag...");
                    _noCollapse = Enum.ToObject(_imGuiWindowFlagsType, 1 << 3);
                }
                if (_noSavedSettings == null)
                {
                    Logger.Debug("Initializing NoSavedSettings flag...");
                    _noSavedSettings = Enum.ToObject(_imGuiWindowFlagsType, 1 << 5);
                }
                if (_leftMouseButton == null)
                {
                    Logger.Debug("Initializing LeftMouseButton flag...");
                    _leftMouseButton = Enum.ToObject(_imGuiMouseButtonType, 0);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize window flags: {ex}");
                return false;
            }
        }

        private void InitializeImGui()
        {
            Logger.Debug("Initializing ImGui...");

            // Log all properties and methods for debugging
            var properties = _imGuiType.GetProperties(BindingFlags.Public | BindingFlags.Static);
            Logger.Debug($"Found {properties.Length} static properties:");
            foreach (var prop in properties)
            {
                Logger.Debug($"- {prop.Name} ({prop.PropertyType.Name})");
            }

            var methods = _imGuiType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            Logger.Debug($"Found {methods.Length} static methods:");
            foreach (var method in methods.DistinctBy(m => m.Name))
            {
                Logger.Debug($"- {method.Name} ({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }

            // Try to get the IO property
            var ioProperty = _imGuiType.GetProperty("IO", BindingFlags.Public | BindingFlags.Static);
            if (ioProperty == null)
            {
                // Try alternative approach - sometimes it's a method
                var getIOMethod = _imGuiType.GetMethod("GetIO", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null);

                if (getIOMethod != null)
                {
                    Logger.Debug("Found GetIO method, using it to get IO");
                    var ioFromMethod = getIOMethod.Invoke(null, null);  // Changed variable name to avoid conflict
                    Logger.Debug("Successfully got IO via GetIO()");
                    return;
                }

                Logger.Error("Failed to find IO property or GetIO method");
                return;
            }

            var ioFromProperty = ioProperty.GetValue(null);  // Changed variable name to avoid conflict
            if (ioFromProperty == null)
            {
                Logger.Error("IO property returned null");
                return;
            }

            // Get required types
            _vector2Type = typeof(Vector2);
            _imGuiCondType = _imGuiAssembly.GetType("ImGuiNET.ImGuiCond");
            _imGuiWindowFlagsType = _imGuiAssembly.GetType("ImGuiNET.ImGuiWindowFlags");

            if (_vector2Type == null || _imGuiCondType == null || _imGuiWindowFlagsType == null)
            {
                Logger.Error("Failed to get required types");
                return;
            }

            // Set up method bindings
            _setNextWindowPosMethod = _imGuiType.GetMethod("SetNextWindowPos",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { _vector2Type, _imGuiCondType, _vector2Type },
                null);

            _setNextWindowSizeMethod = _imGuiType.GetMethod("SetNextWindowSize",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { _vector2Type, _imGuiCondType },
                null);

            _beginMethod = _imGuiType.GetMethod("Begin",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), _imGuiWindowFlagsType },
                null);

            _endMethod = _imGuiType.GetMethod("End",
                BindingFlags.Public | BindingFlags.Static);

            _textMethod = _imGuiType.GetMethod("Text",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            _sameLineMethod = _imGuiType.GetMethod("SameLine",
                BindingFlags.Public | BindingFlags.Static);

            _buttonMethod = _imGuiType.GetMethod("Button",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (_setNextWindowPosMethod == null || _setNextWindowSizeMethod == null ||
                _beginMethod == null || _endMethod == null ||
                _textMethod == null || _sameLineMethod == null ||
                _buttonMethod == null)
            {
                Logger.Error("Failed to get required ImGui methods");
                return;
            }

            // Get the ImGui assembly and types

            _imGuiMouseButtonType = _imGuiAssembly.GetType("ImGuiNET.ImGuiMouseButton");

            if (_imGuiWindowFlagsType == null || _imGuiMouseButtonType == null)
            {
                Logger.Error("Failed to get required ImGui enum types");
                return;
            }

            // Initialize window flags
            try
            {
                if (_noTitleBar == null)
                {
                    Logger.Debug("Initializing NoTitleBar flag...");
                    _noTitleBar = Enum.ToObject(_imGuiWindowFlagsType, 1 << 0);    // NoTitleBar
                }
                if (_noResize == null)
                {
                    Logger.Debug("Initializing NoResize flag...");
                    _noResize = Enum.ToObject(_imGuiWindowFlagsType, 1 << 1);     // NoResize
                }
                if (_noMove == null)
                {
                    Logger.Debug("Initializing NoMove flag...");
                    _noMove = Enum.ToObject(_imGuiWindowFlagsType, 1 << 2);       // NoMove
                }
                if (_noCollapse == null)
                {
                    Logger.Debug("Initializing NoCollapse flag...");
                    _noCollapse = Enum.ToObject(_imGuiWindowFlagsType, 1 << 3);   // NoCollapse
                }
                if (_noSavedSettings == null)
                {
                    Logger.Debug("Initializing NoSavedSettings flag...");
                    _noSavedSettings = Enum.ToObject(_imGuiWindowFlagsType, 1 << 5); // NoSavedSettings
                }

                if (_imGuiMouseButtonType == null)
                {
                    Logger.Debug("Initializing LeftMouseButton flag...");
                    _leftMouseButton = Enum.ToObject(_imGuiMouseButtonType, 0);    // ImGuiMouseButton.Left
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize ImGui enums: {ex}");
                return;
            }

            Logger.Debug("ImGui initialized successfully");

        }

        protected override async Task InitializeInternalAsync()
        {
            Logger.Information("Initializing FFXIV plugin...");

            var loadContext = new PluginLoadContext(AppContext.BaseDirectory);
            _coreSupportAssembly = loadContext.LoadFromAssemblyName(new AssemblyName("CoreLibLaunchSupport"))
                ?? throw new InvalidOperationException("Could not load CoreLibLaunchSupport assembly");
            _imGuiAssembly = loadContext.LoadFromAssemblyName(new AssemblyName("ImGui.NET"))
                ?? throw new InvalidOperationException("Could not load ImGui.NET assembly");
            var numericsAssembly = loadContext.LoadFromAssemblyName(new AssemblyName("System.Numerics"))
                ?? throw new InvalidOperationException("Could not load System.Numerics assembly");

            _networkLogicType = _coreSupportAssembly.GetType("CoreLibLaunchSupport.networklogic")
                ?? throw new InvalidOperationException("Could not load networklogic type");
            // Make sure to set _imGuiType
            _imGuiType = _imGuiAssembly.GetType("ImGuiNET.ImGui")
                ?? throw new InvalidOperationException("Could not load ImGui type");

            Logger.Debug($"ImGui type: {_imGuiType.FullName}");
            Logger.Debug($"ImGui type: {_imGuiType.FullName}");
            _imGuiWindowFlagsType = _imGuiAssembly.GetType("ImGuiNET.ImGuiWindowFlags")
                ?? throw new InvalidOperationException("Could not load ImGuiWindowFlags type");
            _vector4Type = numericsAssembly.GetType("System.Numerics.Vector4")
                ?? throw new InvalidOperationException("Could not load Vector4 type");

            _networkLogic = Activator.CreateInstance(_networkLogicType)
                ?? throw new InvalidOperationException("Could not create networklogic instance");

            _imGuiBeginMethod = _imGuiType.GetMethod(
                    "Begin",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), _imGuiWindowFlagsType },
                    modifiers: null)
                ?? throw new InvalidOperationException("Could not locate ImGui.Begin(string, ImGuiWindowFlags)");

            _imGuiEndMethod = _imGuiType.GetMethod("End", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException("Could not locate ImGui.End()");

            _imGuiTextMethod = _imGuiType.GetMethod("Text", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                ?? throw new InvalidOperationException("Could not locate ImGui.Text(string)");

            _imGuiSameLineMethod = _imGuiType.GetMethod("SameLine", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException("Could not locate ImGui.SameLine()");

            _imGuiSeparatorMethod = _imGuiType.GetMethod("Separator", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException("Could not locate ImGui.Separator()");

            _imGuiTextColoredMethod = _imGuiType.GetMethod(
                    "TextColored",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { _vector4Type, typeof(string) },
                    modifiers: null)
                ?? throw new InvalidOperationException("Could not locate ImGui.TextColored(Vector4, string)");

            _imGuiDefaultWindowFlags = Enum.ToObject(_imGuiWindowFlagsType, 0);

            var checkGateStatus = _networkLogicType.GetMethod("CheckGateStatus")
                ?? throw new InvalidOperationException("Could not find CheckGateStatus method");
            var checkLoginStatus = _networkLogicType.GetMethod("CheckLoginStatus")
                ?? throw new InvalidOperationException("Could not find CheckLoginStatus method");

            var gateStatus = await Task.Run(() => (bool)checkGateStatus.Invoke(_networkLogic, null));
            var loginStatus = await Task.Run(() => (bool)checkLoginStatus.Invoke(_networkLogic, null));

            Logger.Information($"Server Status - Gate: {gateStatus}, Login: {loginStatus}");

            if (!gateStatus)
            {
                Logger.Warning("Game servers unavailable");
            }

            if (!loginStatus)
            {
                Logger.Warning("Login servers unavailable");
            }

            if (gateStatus && loginStatus)
            {
                Logger.Information("All systems operational");
            }

            InitializeImGui();
        }


        public override void RenderImGui()
        {
            if (_imGuiType == null)
            {
                Logger.Error("ImGui type is not initialized");
                return;
            }

            // Ensure window flags are initialized
            if (!EnsureWindowFlagsInitialized())
            {
                Logger.Error("Failed to initialize window flags");
                return;
            }

            try
            {
                // Try to get IO property first
                var ioProperty = _imGuiType.GetProperty("IO", BindingFlags.Public | BindingFlags.Static);
                object io = null;
                PropertyInfo? xProp = null;
                PropertyInfo? yProp = null;



                if (ioProperty != null)
                {
                    io = ioProperty.GetValue(null);
                }
                else
                {
                    // Fall back to GetIO method
                    var getIOMethod = _imGuiType.GetMethod("GetIO", BindingFlags.Public | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (getIOMethod != null)
                    {
                        io = getIOMethod.Invoke(null, null);
                    }
                }

                if (io == null)
                {
                    Logger.Error("Failed to get ImGui IO (both property and method failed)");
                    return;
                }

                //REUSABLE TYPE PROPERTY , DISPLAY SIZE WILL KEEP SEPARATE GETTYPE CALL
                var ioType = io.GetType();


                // Now try to get the DisplaySize property from the IO object
                var displaySizeProperty = io.GetType().GetProperty("DisplaySize"); // KEEP SEPARATE GET TYPE CALL FOR DISPLAY SIZE DO NOT SWAP FOR VARIABLE IO TYPE
                if (displaySizeProperty == null)
                {
                    Logger.Error("Failed to get DisplaySize property from IO");
                    return;
                }

                var displaySize = displaySizeProperty.GetValue(io);
                if (displaySize == null)
                {
                    Logger.Error("DisplaySize is null");
                    return;
                }

                // Convert displaySize to Vector2 if needed
                System.Numerics.Vector2 displaySizeVector;
                if (displaySize is System.Numerics.Vector2 vector)
                {
                    displaySizeVector = vector;
                }
                else
                {
                    // Try to get X and Y properties if direct cast fails
                    xProp = displaySize.GetType().GetProperty("X");
                    yProp = displaySize.GetType().GetProperty("Y");
                    if (xProp == null || yProp == null)
                    {
                        Logger.Error("Failed to get X or Y properties from DisplaySize");
                        return;
                    }
                    displaySizeVector = new System.Numerics.Vector2(
                        Convert.ToSingle(xProp.GetValue(displaySize)),
                        Convert.ToSingle(yProp.GetValue(displaySize))
                    );
                }

                Logger.Debug($"Got display size: {displaySizeVector}");


                // Set window position and size
                float width = 400;
                float height = 300;


                // Set default display size values (1024x768 windowed)
                float displayWidth = 1024f;
                float displayHeight = 768f;

                // Try to get the actual display size, but fall back to defaults if anything fails
                if (xProp != null && yProp != null)
                {
                    try
                    {
                        var xValue = xProp.GetValue(displaySize);
                        var yValue = yProp.GetValue(displaySize);

                        if (xValue != null && yValue != null)
                        {
                            // Ensure the window is smaller than the display
                            var screenWidth = Convert.ToSingle(xValue);
                            var screenHeight = Convert.ToSingle(yValue);

                            // Use the smaller of the two: actual screen size or our default window size
                            displayWidth = Math.Min(displayWidth, screenWidth * 0.9f);  // 90% of screen width
                            displayHeight = Math.Min(displayHeight, screenHeight * 0.9f); // 90% of screen height
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to get display size, using defaults: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warning("Using default window size (1024x768)");
                }


                // Create Vector2 instances using reflection
                var vector2Ctor = typeof(Vector2).GetConstructor(new[] { typeof(float), typeof(float) });
                var windowPos = vector2Ctor.Invoke(new object[] {
            (displayWidth - width) * 0.5f,
            (displayHeight - height) * 0.5f
        });
                var windowSize = vector2Ctor.Invoke(new object[] { width, height });
                var pivot = vector2Ctor.Invoke(new object[] { 0.5f, 0.5f });

                // Get enum values
                var imGuiCondType = _imGuiAssembly.GetType("ImGuiNET.ImGuiCond");
                var imGuiWindowFlagsType = _imGuiAssembly.GetType("ImGuiNET.ImGuiWindowFlags");

                var alwaysValue = Enum.ToObject(imGuiCondType, 1 << 0); // ImGuiCond.Always
                var noCollapse = Enum.ToObject(imGuiWindowFlagsType, 1 << 3); // ImGuiWindowFlags.NoCollapse
                var noSavedSettings = Enum.ToObject(imGuiWindowFlagsType, 1 << 5); // ImGuiWindowFlags.NoSavedSettings
                                                                                   // First, make sure all the flag variables are initialized
                if (_noTitleBar == null || _noResize == null || _noMove == null ||
                    _noCollapse == null || _noSavedSettings == null)
                {
                    Logger.Error("One or more window flags are not initialized");
                    return;
                }

                // Convert all flags to int and combine them
                int windowFlags = (int)_noTitleBar | (int)_noResize | (int)_noMove |
                                 (int)_noCollapse | (int)_noSavedSettings;

                // Convert back to the proper enum type
                var windowFlagsObj = Enum.ToObject(imGuiWindowFlagsType, windowFlags);

                // Set window position and size
                var setNextWindowPosMethod = _imGuiType.GetMethod("SetNextWindowPos",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Vector2), imGuiCondType, typeof(Vector2) },
                    null);

                var setNextWindowSizeMethod = _imGuiType.GetMethod("SetNextWindowSize",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Vector2), imGuiCondType },
                    null);

                var beginMethod = _imGuiType.GetMethod("Begin",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), imGuiWindowFlagsType },
                    null);

                var endMethod = _imGuiType.GetMethod("End",
                    BindingFlags.Public | BindingFlags.Static);

                var textMethod = _imGuiType.GetMethod("Text",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);

                // Set window position and size
                setNextWindowPosMethod?.Invoke(null, new[] { windowPos, alwaysValue, pivot });
                setNextWindowSizeMethod?.Invoke(null, new[] { windowSize, 0 }); // 0 = ImGuiCond.None

                

                // Begin window
                var isOpen = new object[] { true };
                var beginResult = beginMethod?.Invoke(null, new object[] { "FFXIV Launcher", windowFlagsObj });
                // In RenderImGui method, replace the window movement code with:
                var isWindowHoveredMethod = _imGuiType.GetMethod("IsWindowHovered",
    BindingFlags.Public | BindingFlags.Static,
    null,
    Type.EmptyTypes,  // This specifies we want the parameterless overload
    null);
                var isMouseDownMethod = _imGuiType.GetMethod("IsMouseDown",
    BindingFlags.Public | BindingFlags.Static,
    null,
    new[] { _imGuiMouseButtonType },
    new ParameterModifier[0]);
    if (isWindowHoveredMethod == null)
{
    Logger.Error("Failed to get IsWindowHovered method");
    return;
}

if (isMouseDownMethod == null)
{
    Logger.Error("Failed to get IsMouseDown method");
    return;
}


                if ((bool)isWindowHoveredMethod.Invoke(null, null) &&
                    (bool)isMouseDownMethod.Invoke(null, new[] { _leftMouseButton }))
                {
                    // Get the current window position
                    var getWindowPosMethod = _imGuiType.GetMethod("GetWindowPos",
                        BindingFlags.Public | BindingFlags.Static);
                    if (getWindowPosMethod == null)
                    {
                        Logger.Error("Failed to get GetWindowPos method");
                        return;
                    }

                    var currentPos = getWindowPosMethod.Invoke(null, null);
                    if (currentPos == null)
                    {
                        Logger.Error("Failed to get current window position");
                        return;
                    }

                    var mouseDeltaProperty = ioType.GetProperty("MouseDelta");
                    if (mouseDeltaProperty == null)
                    {
                        Logger.Error("Failed to get MouseDelta property");
                        return;
                    }

                    var mouseDelta = mouseDeltaProperty.GetValue(io);
                    if (mouseDelta == null)
                    {
                        Logger.Error("Failed to get mouse delta");
                        return;
                    }

                    // Get the X and Y properties from the Vector2
                    var mouseDeltaX = (float)mouseDelta.GetType().GetProperty("X").GetValue(mouseDelta);
                    var mouseDeltaY = (float)mouseDelta.GetType().GetProperty("Y").GetValue(mouseDelta);
                    var windowPosX = (float)currentPos.GetType().GetProperty("X").GetValue(currentPos);
                    var windowPosY = (float)currentPos.GetType().GetProperty("Y").GetValue(currentPos);

                    // Create new position
                    var newPos = Activator.CreateInstance(currentPos.GetType(),
                        windowPosX + mouseDeltaX,
                        windowPosY + mouseDeltaY);

                    // Set the new window position
                    var setWindowPosMethod = _imGuiType.GetMethod("SetWindowPos",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { currentPos.GetType() },
                        null);

                    if (setWindowPosMethod == null)
                    {
                        Logger.Error("Failed to get SetWindowPos method");
                        return;
                    }

                    setWindowPosMethod.Invoke(null, new[] { newPos });
                }
           if (beginResult is bool isWindowOpen && isWindowOpen)
{
    try
    {
        // --- Получение свойств окна (Позиция и Размер) ---
        // *** ФИКС #1: Получение абсолютной позиции окна на экране ***
        var getWindowPosMethod = _imGuiType.GetMethod("GetWindowPos", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (getWindowPosMethod == null) { Logger.Error("Failed to get GetWindowPos method"); return; }
        // Ошибка CS0136 исправлена: используем уже существующую переменную `windowPos`
        windowPos = getWindowPosMethod.Invoke(null, null);

        var windowSizeMethod = _imGuiType.GetMethod("GetWindowSize", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (windowSizeMethod == null) { Logger.Error("Failed to get GetWindowSize method"); return; }
        // Ошибка CS0136 исправлена: используем уже существующую переменную `windowSize`
        windowSize = windowSizeMethod.Invoke(null, null);

        // --- Получение правильного списка для отрисовки ---
        // *** ФИКС #2: Используем GetWindowDrawList, а не GetBackgroundDrawList ***
        var getWindowDrawListMethod = _imGuiType.GetMethod("GetWindowDrawList", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (getWindowDrawListMethod == null) { Logger.Error("Failed to find GetWindowDrawList method"); return; }
        var drawList = getWindowDrawListMethod.Invoke(null, null);
        
        // *** ФИКС #3: Получаем тип ImDrawFlags через рефлексию ***
        var imDrawFlagsType = _imGuiAssembly.GetType("ImGuiNET.ImDrawFlags");
        if (imDrawFlagsType == null) { Logger.Error("Failed to get ImDrawFlags type"); return; }

        // --- Получение методов для отрисовки через рефлексию ---
        var addRectFilledMethod = drawList.GetType().GetMethod("AddRectFilled", new[] { typeof(Vector2), typeof(Vector2), typeof(uint), typeof(float), imDrawFlagsType });
        var colorConvertMethod = _imGuiType.GetMethod("ColorConvertFloat4ToU32", new[] { typeof(Vector4) });
        var calcTextSizeMethod = _imGuiType.GetMethod("CalcTextSize", new[] { typeof(string) });
        var setCursorPosMethod = _imGuiType.GetMethod("SetCursorPos", new[] { typeof(Vector2) });
        var textColoredMethod = _imGuiType.GetMethod("TextColored", new[] { typeof(Vector4), typeof(string) });

        // --- Отрисовка кастомного фона ---
        var bgColor = new Vector4(0.08f, 0.08f, 0.11f, 1.0f);
        var bgColorU32 = (uint)colorConvertMethod.Invoke(null, new object[] { bgColor });
        var rectBottomRight = new Vector2(((Vector2)windowPos).X + ((Vector2)windowSize).X, ((Vector2)windowPos).Y + ((Vector2)windowSize).Y);
        
        if(addRectFilledMethod != null)
        {
            var imDrawFlagsNone = Enum.ToObject(imDrawFlagsType, 0); // ImDrawFlags.None
            addRectFilledMethod.Invoke(drawList, new object[] { windowPos, rectBottomRight, bgColorU32, 8.0f, imDrawFlagsNone });
        }

        // --- Отрисовка заголовка (используем стандартные виджеты) ---
        var titleText = "FFXIV Launcher";
        var titleSize = (Vector2)calcTextSizeMethod.Invoke(null, new object[] { titleText });
        var titlePosRelative = new Vector2((((Vector2)windowSize).X - titleSize.X) * 0.5f, ((Vector2)windowSize).Y * 0.2f);
        setCursorPosMethod.Invoke(null, new object[] { titlePosRelative });
        textColoredMethod.Invoke(null, new object[] { new Vector4(0.9f, 0.9f, 0.9f, 1.0f), titleText });

        // --- Отрисовка статуса сервера ---
        var gateStatus = (bool)_networkLogicType.GetMethod("CheckGateStatus").Invoke(_networkLogic, null);
        var statusText = gateStatus ? "Server Status: Online" : "Server Status: Offline";
        var statusColor = gateStatus ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f) : new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
        var statusSize = (Vector2)calcTextSizeMethod.Invoke(null, new object[] { statusText });
        var statusPosRelative = new Vector2((((Vector2)windowSize).X - statusSize.X) * 0.5f, titlePosRelative.Y + titleSize.Y + 20);
        setCursorPosMethod.Invoke(null, new object[] { statusPosRelative });
        textColoredMethod.Invoke(null, new object[] { statusColor, statusText });

        // --- Отрисовка спиннера загрузки (используя абсолютные координаты) ---
        var spinnerRadius = 20.0f;
        var spinnerCenterAbs = new Vector2(
            ((Vector2)windowPos).X + ((Vector2)windowSize).X * 0.5f,
            ((Vector2)windowPos).Y + statusPosRelative.Y + statusSize.Y + 40
        );

        var time = (float)DateTime.Now.TimeOfDay.TotalSeconds;
        var spinnerAngle = time * 5.0f;
        var arcColor = (uint)colorConvertMethod.Invoke(null, new object[] { new Vector4(0.8f, 0.8f, 1f, 1f) });

        var addArcMethod = drawList.GetType().GetMethod("AddArc", new[] { typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(uint), typeof(int), typeof(float) });
        if (addArcMethod != null)
        {
            addArcMethod.Invoke(drawList, new object[] { spinnerCenterAbs, spinnerRadius, spinnerAngle - 2.0f, spinnerAngle + 2.0f, arcColor, 20, 2.5f });
        }
    }
    catch (Exception ex)
    {
        Logger.Error($"Error in window rendering: {ex}");
    }
    finally
    {
        endMethod?.Invoke(null, null);
    }
}
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in RenderImGui: {ex}");
            }
        }



        protected override async Task<bool> LaunchGameInternalAsync(GameLaunchParameters parameters)
        {
            try
            {
                Logger.Debug($"Launch parameters received: GamePath={parameters.GamePath}");
                Logger.Debug($"Environment variables count: {parameters.EnvironmentVariables.Count}");
                foreach (var kvp in parameters.EnvironmentVariables)
                {
                    Logger.Debug($"Environment variable: {kvp.Key}={kvp.Value}");
                }

                if (parameters.EnvironmentVariables.TryGetValue("FFXIV_USERNAME", out var username) &&
                    parameters.EnvironmentVariables.TryGetValue("FFXIV_PASSWORD", out var password) &&
                    parameters.EnvironmentVariables.TryGetValue("FFXIV_OTP", out var otp))
                {
                    Logger.Debug("All required credentials found");
                    var getRealSid = _networkLogicType.GetMethod("GetRealSid");
                    var launchGameAsync = _networkLogicType.GetMethod("LaunchGameAsync");

                    Logger.Debug("Attempting to obtain session ID");
                    var sid = await Task.Run(() => (string)getRealSid.Invoke(_networkLogic, new object[]
                    {
                parameters.GamePath,
                username,
                password,
                otp,
                parameters.IsSteam
                    }));

                    if (sid == "BAD")
                    {
                        throw new Exception("Failed to obtain valid session ID");
                    }

                    Logger.Debug("Successfully obtained session ID");
                    var process = await Task.Run(() => launchGameAsync.Invoke(_networkLogic, new object[]
                    {
                parameters.GamePath,
                sid,
                parameters.Language,
                parameters.DirectX11,
                parameters.ExpansionLevel,
                parameters.IsSteam,
                parameters.Region
                    }));

                    return process != null;
                }
                else
                {
                    Logger.Error("Missing credentials:");
                    Logger.Error($"Username present: {parameters.EnvironmentVariables.ContainsKey("FFXIV_USERNAME")}");
                    Logger.Error($"Password present: {parameters.EnvironmentVariables.ContainsKey("FFXIV_PASSWORD")}");
                    Logger.Error($"OTP present: {parameters.EnvironmentVariables.ContainsKey("FFXIV_OTP")}");
                    throw new ArgumentException("Missing required credentials in environment variables");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to launch FFXIV", ex);
                return false;
            }
        }


        protected override Task ShutdownInternalAsync()
        {
            Logger.Information("Shutting down FFXIV plugin...");
            return Task.CompletedTask;
        }

        protected override Task<bool> ValidateConfigurationInternalAsync()
        {
            try
            {
                // Initialize config if empty
                if (!_config.ContainsKey("GamePath"))
                {
                    _config["GamePath"] = Path.Combine(AppContext.BaseDirectory, "game");
                }

                if (!Directory.Exists(Path.Combine(_config["GamePath"], "game")))
                {
                    Logger.Warning("FFXIV game directory not found, will be validated at launch time");
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error("Configuration validation failed", ex);
                return Task.FromResult(false);
            }
        }


    }
}
