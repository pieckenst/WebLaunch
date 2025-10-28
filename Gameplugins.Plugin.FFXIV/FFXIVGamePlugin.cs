using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using El_Garnan_Plugin_Loader.Base;
using El_Garnan_Plugin_Loader.Interfaces;
using El_Garnan_Plugin_Loader.Models;
using El_Garnan_Plugin_Loader;
using System.Numerics;

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

        // Now try to get the DisplaySize property from the IO object
        var displaySizeProperty = io.GetType().GetProperty("DisplaySize");
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
        var windowFlags = (int)noCollapse | (int)noSavedSettings;

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
        var beginResult = beginMethod?.Invoke(null, new object[] { "FFXIV Launcher", windowFlags });
        
        if (beginResult is bool isWindowOpen && isWindowOpen)
        {
            try
            {
                // Window content
                textMethod?.Invoke(null, new object[] { "Server Status: " + (_networkLogic != null ? "Online" : "Offline") });
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
