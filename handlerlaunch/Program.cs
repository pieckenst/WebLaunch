using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using El_Garnan_Plugin_Loader;
using El_Garnan_Plugin_Loader.Interfaces;
using WebLaunch.Bridge;
using WebLaunch.Core;
using handlerlaunch;

namespace WMConsole;

internal class Program
{
    internal static ILaunchService? Launcher { get; private set; }
    internal static DesktopTrustStore? Trust { get; private set; }
    public static string GetExpansionFolder(byte expansionId) => expansionId == 0 ? "ffxiv" : $"ex{expansionId}";
    public static string ReturnXpacNum(ushort expansionId) => GetExpansionFolder(checked((byte)expansionId));
    public static string? TextFollowing(string? text, string? value) => text is not null && value is not null && text.IndexOf(value, StringComparison.Ordinal) is var index && index >= 0 ? text[(index + value.Length)..] : null;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("WebLaunch [--console [--quiet]] [--debug] [--install | launch URI]");
            Console.WriteLine("Console mode pairs by typing matching codes. Quiet mode accepts existing pairings only.");
            Console.WriteLine("--debug records sanitized lifecycle diagnostics in %LOCALAPPDATA%\\WebLaunch\\Logs. Install/repair enables Windows notifications.");
            return;
        }
        var console = args.Contains("--console", StringComparer.Ordinal);
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        var debug = args.Contains("--debug", StringComparer.Ordinal);
        DesktopDiagnostics.Configure(debug);
        args = args.Where(a => a is not ("--console" or "--quiet" or "--debug")).ToArray();
        if (quiet && !console) { Console.Error.WriteLine("--quiet requires --console."); return; }
        if (quiet) { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); }
        if (args.Length > 1 || (args.Length == 1 && args[0].Length > 8192))
        { Console.Error.WriteLine("Supply at most one launch URI or --install."); return; }
        try
        {
            if (args.Length == 1 && BootstrapRequest.TryParse(args[0], out var bootstrap))
            {
                if (quiet || (console && bootstrap!.Mode != "console")) throw new ArgumentException();
                console = bootstrap!.Mode == "console";
            }
        }
        catch { Console.Error.WriteLine("Invalid desktop connection link."); return; }
        if (console && args.FirstOrDefault() == "--install")
        {
            try { RegisterProtocolHandler(); Console.WriteLine("Browser launch link installed."); }
            catch { Console.Error.WriteLine("Browser launch link installation failed."); }
            return;
        }
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var pipe = "WebLaunch-" + user;
        using var mutex = new Mutex(false, @"Local\" + pipe);
        bool owns;
        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns)
        {
            if (debug) Console.WriteLine("Restart the running launcher with --debug to enable detailed diagnostics.");
            try
            {
                using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                connection.Connect(3000);
                using var writer = new StreamWriter(connection) { AutoFlush = true };
                if (args.Length > 1 || (args.Length == 1 && args[0].Length > 8192)) throw new ArgumentException();
                writer.WriteLine(args.FirstOrDefault() ?? "");
            }
            catch
            {
                if (console) Console.Error.WriteLine("The launcher is already starting. Retry in a few seconds.");
                else MessageBox.Show("The launcher is already starting. Try Connect again in a few seconds.", "WebLaunch");
            }
            return;
        }
        try
        {
            if (console) RunConsoleAsync(args.FirstOrDefault() ?? "", pipe, quiet).GetAwaiter().GetResult();
            else RunApplication(args, pipe);
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static async Task RunConsoleAsync(string argument, string pipe, bool quiet)
    {
        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += stop;
        try
        {
            await using var runtime = await DesktopRuntime.StartAsync(new ConsolePairingPrompt(Console.In, Console.Out, quiet),
                status => Console.WriteLine(status.Message), lifetime.Token, quiet ? "quiet" : "console");
            Trust = runtime.Trust; Launcher = runtime.Launcher;
            async Task Handle(string uri)
            {
                if (uri == "") return;
                try
                {
                    if (BootstrapRequest.TryParse(uri, out var bootstrap))
                    {
                        if (bootstrap!.Mode != "console") Console.WriteLine("Console mode is already running. Stop it before opening GUI mode.");
                        return;
                    }
                }
                catch (ArgumentException) { Console.Error.WriteLine("Invalid desktop connection link."); return; }
                if (uri == "--install") { RegisterProtocolHandler(); return; }
                LaunchRequest? request = null;
                try
                {
                    request = LegacyProtocol.Parse(uri, Trust.LegacyUntil, DateTimeOffset.UtcNow);
                    await Launcher.LaunchAsync(request, new InlineProgress<LaunchStatus>(_ => { }), lifetime.Token);
                    Console.WriteLine("Game process started.");
                }
                catch (OperationCanceledException) { Console.WriteLine("Launch cancelled."); }
                catch { Console.Error.WriteLine("Launch failed or legacy links are disabled. Use the paired browser connection."); }
                finally { request?.ClearCredentials(); }
            }
            Console.WriteLine("WebLaunch ready. Connect from your browser; press Ctrl+C to stop.");
            var listening = ListenAsync(pipe, Handle, lifetime.Token);
            try
            {
                await Handle(argument);
                await Task.Delay(Timeout.Infinite, lifetime.Token);
            }
            finally { await lifetime.CancelAsync(); await listening; }
        }
        catch (OperationCanceledException) { }
        catch { Environment.ExitCode = 1; Console.Error.WriteLine("Desktop connection could not start. Check port 47832 and the installation."); }
        finally { Console.CancelKeyPress -= stop; }
    }
    private static void RunApplication(string[] args, string pipe)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { Title = "WebLaunch", Width = 540, Height = 480, MinWidth = 420, MinHeight = 320 };
        var content = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Starting launcher…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        content.Children.Add(new TextBlock { Text = "WebLaunch desktop connection", FontSize = 22, Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(status);
        var register = new Button { Content = "Install / repair browser launch link", Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(8) };
        content.Children.Add(register);
        var forget = new Button { Content = "Forget all paired browsers", Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(8) };
        content.Children.Add(forget);
        var legacy = new CheckBox { Content = "Allow legacy credential links for 24 hours", Margin = new Thickness(0, 12, 0, 6) };
        content.Children.Add(legacy);
        content.Children.Add(new TextBlock { Text = "Install / repair enables Windows progress notifications. Logs: %LOCALAPPDATA%\\WebLaunch\\Logs. Start with --debug for plugin lifecycle details.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        content.Children.Add(new TextBlock { Text = "Legacy links expose login data in URLs and operating-system launch arguments. Prefer Connect on the updated website.", TextWrapping = TextWrapping.Wrap });
        window.Content = content;
        using var lifetime = new CancellationTokenSource();
        DesktopRuntime? runtime = null;
        BridgeHost? bridge = null;
        Task? pipeTask = null;
        bool closing = false;
        void Report(string message) => application.Dispatcher.BeginInvoke(() => status.Text = message);
        async Task HandleAsync(string argument)
        {
            window.Show(); window.Activate();
            if (argument == "") return;
            try
            {
                if (BootstrapRequest.TryParse(argument, out var bootstrap))
                {
                    if (bootstrap!.Mode != "gui") Report("GUI mode is already running. Close this window, then open console mode from the browser.");
                    return;
                }
            }
            catch (ArgumentException) { Report("Invalid desktop connection link."); return; }
            if (argument == "--install") { RegisterProtocolHandler(); Report("Browser link installed. Choose Connect on the website."); return; }
            LaunchRequest? request = null;
            try
            {
                request = LegacyProtocol.Parse(argument, Trust!.LegacyUntil, DateTimeOffset.UtcNow);
                await Launcher!.LaunchAsync(request, new InlineProgress<LaunchStatus>(_ => { }), lifetime.Token);
                Report("Game process started.");
            }
            catch (InvalidOperationException) { Report("Legacy links are disabled or the launch failed. Use Connect on the updated website."); }
            catch (OperationCanceledException) { Report("Launch cancelled."); }
            catch { Report("Invalid launch link or launch failure. Check the selected game folder and sign-in details."); }
            finally { request?.ClearCredentials(); }
        }
        application.Startup += async (_, _) =>
        {
            window.Show();
            try
            {
                runtime = await DesktopRuntime.StartAsync(new DesktopPairingPrompt(window), s => Report(s.Message), lifetime.Token);
                Trust = runtime.Trust; Launcher = runtime.Launcher; bridge = runtime.Bridge;
                status.Text = "Ready. Choose Connect on the website. Keep this window open while launching.";
                legacy.IsChecked = Trust.LegacyUntil > DateTimeOffset.UtcNow;
                pipeTask = ListenAsync(pipe, a => application.Dispatcher.InvokeAsync(() => HandleAsync(a)).Task.Unwrap(), lifetime.Token);
                if (args.Length > 1) throw new ArgumentException();
                await HandleAsync(args.FirstOrDefault() ?? "");
            }
            catch
            {
                status.Text = "The desktop connection could not start. Another service may be using port 47832, or the installation is incomplete. Close other launcher instances and repair the installation.";
                legacy.IsEnabled = false; forget.IsEnabled = false;
            }
        };
        register.Click += (_, _) => { try { RegisterProtocolHandler(); status.Text = "Browser link installed. Choose Connect on the website."; } catch { status.Text = "Registration failed. Extract the complete bundle to a writable folder and retry."; } };
        forget.Click += async (_, _) => { if (bridge is not null) { await bridge.ForgetBrowsersAsync(); status.Text = "Browsers forgotten. Pair again before launching."; } };
        legacy.Click += (_, _) =>
        {
            var enabled = legacy.IsChecked == true;
            if (enabled && MessageBox.Show(window, "Old links expose your password and OTP in launch arguments. Enable for the next 24 hours?", "Legacy login risk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                enabled = false;
            try { Trust?.SetLegacy(enabled); legacy.IsChecked = enabled; }
            catch { legacy.IsChecked = false; status.Text = "Could not save the legacy setting."; }
        };
        window.Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            status.Text = "Stopping active work…";
            try
            {
                await lifetime.CancelAsync();
                if (pipeTask is not null) await pipeTask;
                if (runtime is not null) await runtime.DisposeAsync();
            }
            finally { application.Shutdown(); }
        };
        application.Run();
    }
    public static void RegisterProtocolHandler()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\HandleWebRequest");
        key.SetValue("", "URL:WebLaunch"); key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{executable}\" \"%1\"");
        NotificationRegistration.Install(executable);
        DesktopDiagnostics.Write(DiagnosticEvent.RegistrationComplete);
    }
    private static async Task ListenAsync(string name, Func<string, Task> handle, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(server);
                var buffer = new char[8193];
                var count = 0;
                try
                {
                    while (count < buffer.Length)
                    {
                        var read = await reader.ReadAsync(buffer.AsMemory(count, 1), timeout.Token);
                        if (read == 0 || buffer[count] == '\n') break;
                        count += read;
                    }
                    if (count < buffer.Length) await handle(new string(buffer, 0, count).TrimEnd('\r'));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
    }
}

public sealed class DesktopPairingPrompt(Window owner) : IPairingPrompt
{
    public Task<bool> ConfirmAsync(string origin, string code, CancellationToken token) => owner.Dispatcher.InvokeAsync(() => ShowAsync(origin, code, token)).Task.Unwrap();
    private Task<bool> ShowAsync(string origin, string code, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window { Owner = owner, Title = "Pair browser with WebLaunch", Width = 450, Height = 290, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"Pair {origin}?\nConfirm only if the browser shows the same code.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = code, FontSize = 36, Margin = new Thickness(0, 20, 0, 20) });
        var confirm = new Button { Content = "The codes match", Padding = new Thickness(8) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(confirm); panel.Children.Add(cancel); window.Content = panel;
        confirm.Click += (_, _) => { result.TrySetResult(true); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Closed += (_, _) => result.TrySetResult(false);
        var registration = token.Register(() => owner.Dispatcher.BeginInvoke(() => { result.TrySetResult(false); window.Close(); }));
        _ = result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        if (token.IsCancellationRequested) { registration.Dispose(); return Task.FromResult(false); }
        window.Show();
        return result.Task;
    }
}

// Untrusted plugin diagnostic strings must never become credential-bearing logs.
public class ConsoleLogger : ILogger
{
    public void Debug(string message) => DesktopDiagnostics.Write(DiagnosticEvent.PluginDebug);
    public void Information(string message) => DesktopDiagnostics.Write(DiagnosticEvent.PluginInformation);
    public void Warning(string message) => DesktopDiagnostics.Write(DiagnosticEvent.PluginWarning);
    public void Error(string message) => DesktopDiagnostics.Write(DiagnosticEvent.PluginError);
    public void Error(string message, Exception ex) => DesktopDiagnostics.Write(DiagnosticEvent.PluginError);
}
