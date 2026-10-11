using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebLaunch.Core;

namespace WebLaunch.Bridge;

public interface IBridgeTrustStore
{
    ECDsa Identity { get; }
    bool IsTrusted(string origin, string fingerprint);
    void Trust(string origin, string fingerprint);
    void ForgetAll();
}
public interface IPairingPrompt
{
    Task<bool> ConfirmAsync(string origin, string code, CancellationToken cancellationToken);
}
public interface IGameFolderPicker
{
    Task<string?> SelectAsync(CancellationToken cancellationToken);
}
public sealed class BridgeCommand
{
    public string Action { get; set; } = "";
    public string Code { get; set; } = "";
    public LaunchRequest? Launch { get; set; }
}
public sealed record BridgeReply(bool Paired, LaunchStatus Status, string DesktopMode = "gui", bool CanBrowseFolders = false, string? FolderPath = null);

public sealed class BridgeHost : IAsyncDisposable
{
    public const int Port = 47832;
    private readonly IBridgeTrustStore trust;
    private readonly IPairingPrompt prompt;
    private readonly ILaunchService launcher;
    private readonly HashSet<string> origins;
    private readonly string desktopMode;
    private readonly IGameFolderPicker? folderPicker;
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, byte> usedRequestIds = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim admission = new(1, 1);
    private WebApplication? app;
    private Task? reaper;
    private DateTimeOffset pairingRetryAfter;
    private int pairingFailures;

    public BridgeHost(IBridgeTrustStore trust, IPairingPrompt prompt, ILaunchService launcher, IEnumerable<string>? developmentOrigins = null, string desktopMode = "gui", IGameFolderPicker? folderPicker = null)
    {
        if (desktopMode is not ("gui" or "console" or "quiet")) throw new ArgumentException("Invalid desktop mode.");
        this.desktopMode = desktopMode; this.folderPicker = folderPicker;
        this.trust = trust; this.prompt = prompt; this.launcher = launcher;
        origins = new(StringComparer.Ordinal) { "https://pieckenst.github.io" };
        foreach (var origin in developmentOrigins ?? [])
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw new ArgumentException("Development origins must be explicit loopback origins.");
            origins.Add(origin);
        }
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // HTTP logs must never contain launch bodies, session URLs, or request exceptions.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, Port);
            server.Limits.MaxRequestBodySize = 65_536;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Host.Value != $"127.0.0.1:{Port}" || !origins.Contains(origin) ||
                context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Method == "OPTIONS")
            {
                context.Response.Headers.AccessControlAllowMethods = "POST";
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                context.Response.StatusCode = 204; return;
            }
            if (context.Request.Method != "POST" || !string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 415; return; }
            try { await next(context); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or CryptographicException or InvalidDataException or BadHttpRequestException)
            { context.Response.StatusCode = 400; }
            catch (OperationCanceledException) { context.Response.StatusCode = 408; }
        });
        app.MapPost("/v2/hello", HelloAsync);
        app.MapPost("/v2/session/{id}", MessageAsync);
        await app.StartAsync(cancellationToken);
        reaper = ReapAsync();
    }
    private async Task HelloAsync(HttpContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var hello = await context.Request.ReadFromJsonAsync<ClientHello>(timeout.Token) ?? throw new InvalidDataException();
        var origin = context.Request.Headers.Origin.ToString();
        BridgeCrypto.ValidateHello(origin, hello);
        await admission.WaitAsync(timeout.Token);
        try
        {
            var fingerprint = BridgeCrypto.Fingerprint(hello.Identity);
            var known = trust.IsTrusted(origin, fingerprint);
            if (sessions.Count >= 16 || usedRequestIds.Count >= 4096 ||
                (!known && (DateTimeOffset.UtcNow < pairingRetryAfter || sessions.Values.Any(s => !s.Paired && !s.IsExpired))))
            { context.Response.StatusCode = 429; return; }
            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var clientKey = ECDiffieHellman.Create();
            var publicBytes = Convert.FromBase64String(hello.Ephemeral);
            clientKey.ImportSubjectPublicKeyInfo(publicBytes, out var read);
            if (read != publicBytes.Length || clientKey.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException();
            var response = new ServerHello(Guid.NewGuid().ToString("N"), Convert.ToBase64String(trust.Identity.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "", known);
            var transcript = BridgeCrypto.Transcript(origin, hello, response);
            response = response with { Signature = BridgeCrypto.Sign(trust.Identity, transcript) };
            var secret = ephemeral.DeriveRawSecretAgreement(clientKey.PublicKey);
            Session session;
            try { session = new(origin, fingerprint, BridgeCrypto.PairingCode(transcript), new SecureChannel(secret, transcript, true), lifetime.Token); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            session.Paired = known;
            session.Approval = known ? Task.FromResult(true) : prompt.ConfirmAsync(origin, session.Code, session.PairingTimeout.Token);
            sessions[response.SessionId] = session;
            await context.Response.WriteAsJsonAsync(response, timeout.Token);
        }
        finally { admission.Release(); }
    }
    private async Task MessageAsync(HttpContext context, string id)
    {
        if (!sessions.TryGetValue(id, out var session) || session.Origin != context.Request.Headers.Origin.ToString() || session.IsExpired)
        { context.Response.StatusCode = 410; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await session.Gate.WaitAsync(timeout.Token);
        try
        {
            if (session.IsExpired) { context.Response.StatusCode = 410; return; }
            var envelope = await context.Request.ReadFromJsonAsync<EncryptedMessage>(timeout.Token) ?? throw new InvalidDataException();
            var command = session.Channel.Decrypt<BridgeCommand>(envelope);
            if (command.Action == "disconnect")
            {
                session.Expires = DateTimeOffset.MinValue;
                session.JobCancellation.Cancel(); session.PairingTimeout.Cancel();
                await context.Response.WriteAsJsonAsync(session.Channel.Encrypt(new BridgeReply(false,
                    new("disconnected", "Connection closed.", true), desktopMode)), timeout.Token);
                return;
            }
            if (!session.Paired)
            {
                if (command.Action != "confirm" || command.Code != session.Code || !await session.Approval.WaitAsync(timeout.Token) || session.IsExpired)
                {
                    session.Expires = DateTimeOffset.MinValue;
                    if (Interlocked.Increment(ref pairingFailures) >= 3) pairingRetryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                    context.Response.StatusCode = 403; return;
                }
                await admission.WaitAsync(timeout.Token);
                try { trust.Trust(session.Origin, session.Fingerprint); pairingFailures = 0; }
                finally { admission.Release(); }
                session.Paired = true;
            }
            session.Expires = DateTimeOffset.UtcNow.AddMinutes(10);
            string? folderPath = null;
            LaunchStatus? replyStatus = null;
            switch (command.Action)
            {
                case "confirm": break;
                case "launch":
                    if (session.Job is not null) throw new InvalidDataException("A launch was already submitted.");
                    var request = command.Launch ?? throw new InvalidDataException();
                    request.Validate();
                    if (!usedRequestIds.TryAdd(request.RequestId, 0)) throw new InvalidDataException("Launch request already used.");
                    session.Status = new("connecting", "Launch request accepted.");
                    session.Job = RunLaunchAsync(session, request);
                    break;
                case "browse":
                    if (folderPicker is null) { replyStatus = new("unavailable", "Update the desktop launcher to choose folders, or enter the full path."); break; }
                    if (session.Job is not null) throw new InvalidDataException("Reconnect before selecting another folder.");
                    try
                    {
                        folderPath = await folderPicker.SelectAsync(timeout.Token);
                        if (folderPath is not null) LaunchRequest.ValidateGamePath(folderPath);
                        replyStatus = new("folder", folderPath is null ? "Folder selection cancelled." : "Installation folder selected.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { folderPath = null; replyStatus = new("failed", "Could not select a local folder. Close any open folder chooser and retry, or enter the full path."); }
                    break;
                case "status": break;
                case "cancel": session.JobCancellation.Cancel(); break;
                default: throw new InvalidDataException("Unsupported command.");
            }
            await context.Response.WriteAsJsonAsync(session.Channel.Encrypt(new BridgeReply(session.Paired, replyStatus ?? session.Status, desktopMode, folderPicker is not null, folderPath)), timeout.Token);
        }
        finally { session.Gate.Release(); }
    }
    private async Task RunLaunchAsync(Session session, LaunchRequest request)
    {
        try
        {
            await launcher.LaunchAsync(request, new InlineProgress<LaunchStatus>(s => session.Status = s), session.JobCancellation.Token);
            session.Status = new("launched", "Game process started.", true, true);
        }
        catch (OperationCanceledException) { session.Status = new("cancelled", "Launch cancelled.", true); }
        catch { session.Status = new("failed", "Launch failed. Check the installation, sign-in details, and desktop launcher status, then retry.", true); }
        finally { request.ClearCredentials(); }
    }
    private async Task ReapAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                foreach (var pair in sessions.Where(p => p.Value.IsExpired))
                    if (sessions.TryRemove(pair.Key, out var session)) await session.DisposeAsync();
        }
        catch (OperationCanceledException) { }
    }
    public async Task ForgetBrowsersAsync()
    {
        await admission.WaitAsync();
        try
        {
            trust.ForgetAll();
            foreach (var session in sessions.Values) { session.Expires = DateTimeOffset.MinValue; session.JobCancellation.Cancel(); }
        }
        finally { admission.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (reaper is not null) await reaper;
        foreach (var session in sessions.Values) await session.DisposeAsync();
        sessions.Clear(); lifetime.Dispose(); admission.Dispose();
    }
    private sealed class Session : IAsyncDisposable
    {
        public readonly string Origin, Fingerprint, Code;
        public readonly SecureChannel Channel;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly CancellationTokenSource PairingTimeout, JobCancellation;
        public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(10);
        private readonly DateTimeOffset pairingExpires = DateTimeOffset.UtcNow.AddMinutes(2);
        public bool IsExpired => DateTimeOffset.UtcNow >= Expires || (!Paired && DateTimeOffset.UtcNow >= pairingExpires);
        public bool Paired;
        public Task<bool> Approval = Task.FromResult(false);
        public Task? Job;
        public LaunchStatus Status = new("ready", "Connected to the desktop launcher.");
        public Session(string origin, string fingerprint, string code, SecureChannel channel, CancellationToken lifetime)
        {
            Origin = origin; Fingerprint = fingerprint; Code = code; Channel = channel;
            PairingTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            PairingTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            JobCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            JobCancellation.CancelAfter(TimeSpan.FromHours(2));
        }
        public async ValueTask DisposeAsync()
        {
            await JobCancellation.CancelAsync(); await PairingTimeout.CancelAsync();
            if (Job is not null) await Job;
            await Gate.WaitAsync();
            try { Channel.Dispose(); } finally { Gate.Release(); }
            JobCancellation.Dispose(); PairingTimeout.Dispose();
        }
    }
}
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
