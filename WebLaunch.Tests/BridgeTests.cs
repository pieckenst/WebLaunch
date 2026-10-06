using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using WebLaunch.Bridge;
using WebLaunch.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace WebLaunch.Tests;

public sealed class BridgeTests
{
    private const string Origin = "https://pieckenst.github.io";
    private sealed class TrustStore : IBridgeTrustStore, IDisposable
    {
        public ECDsa Identity { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly HashSet<string> keys = [];
        public bool IsTrusted(string origin, string fingerprint) => keys.Contains(origin + fingerprint);
        public void Trust(string origin, string fingerprint) => keys.Add(origin + fingerprint);
        public void ForgetAll() => keys.Clear();
        public void Dispose() => Identity.Dispose();
    }
    private sealed class Prompt(bool accepted) : IPairingPrompt
    {
        public Task<bool> ConfirmAsync(string origin, string code, CancellationToken cancellationToken) => Task.FromResult(accepted);
    }
    private sealed class Launcher : ILaunchService
    {
        public int Calls;
        public bool Fail;
        public bool Block;
        public async Task LaunchAsync(LaunchRequest request, IProgress<LaunchStatus> progress, CancellationToken cancellationToken)
        {
            Calls++; progress.Report(new("authenticating", "Signing in…"));
            if (Fail) throw new InvalidOperationException("synthetic-secret-must-not-appear");
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
    private sealed class Client : IDisposable
    {
        public HttpClient Http { get; } = new() { BaseAddress = new Uri($"http://127.0.0.1:{BridgeHost.Port}") };
        public string Session = "", Code = "";
        public SecureChannel Channel = null!;
        public Client() => Http.DefaultRequestHeaders.Add("Origin", Origin);
        public async Task ConnectAsync()
        {
            using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var hello = new ClientHello(2, Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "");
            hello = hello with { Signature = BridgeCrypto.Sign(identity, BridgeCrypto.ClientTranscript(Origin, hello)) };
            using var response = await Http.PostAsJsonAsync("/v2/hello", hello);
            response.EnsureSuccessStatusCode();
            var server = (await response.Content.ReadFromJsonAsync<ServerHello>())!;
            var transcript = BridgeCrypto.Transcript(Origin, hello, server);
            Assert.True(BridgeCrypto.Verify(server.Identity, transcript, server.Signature));
            using var peer = ECDiffieHellman.Create(); peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(server.Ephemeral), out _);
            var secret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
            try { Channel = new SecureChannel(secret, transcript, false); } finally { CryptographicOperations.ZeroMemory(secret); }
            Session = server.SessionId; Code = BridgeCrypto.PairingCode(transcript);
        }
        public Task<HttpResponseMessage> Send(BridgeCommand command) => Http.PostAsJsonAsync("/v2/session/" + Session, Channel.Encrypt(command));
        public async Task<BridgeReply> Exchange(BridgeCommand command)
        {
            using var response = await Send(command); response.EnsureSuccessStatusCode();
            var envelope = (await response.Content.ReadFromJsonAsync<EncryptedMessage>())!;
            return Channel.Decrypt<BridgeReply>(envelope);
        }
        public void Dispose() { Channel?.Dispose(); Http.Dispose(); }
    }
    [Fact] public async Task PairThenLaunchAndRejectDuplicateOrReplay()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher, desktopMode: "console"); await host.StartAsync();
        using var client = new Client(); await client.ConnectAsync();
        var confirmation = await client.Exchange(new() { Action = "confirm", Code = client.Code });
        Assert.True(confirmation.Paired); Assert.Equal("console", confirmation.DesktopMode);
        var request = new LaunchRequest { Game = "spellborn", GamePath = @"C:\Games" };
        var reply = await client.Exchange(new() { Action = "launch", Launch = request });
        Assert.True(reply.Status.Success); Assert.Equal(1, launcher.Calls);
        using var duplicate = await client.Send(new() { Action = "launch", Launch = request }); Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var envelope = client.Channel.Encrypt(new BridgeCommand { Action = "status" });
        using var first = await client.Http.PostAsJsonAsync("/v2/session/" + client.Session, envelope); first.EnsureSuccessStatusCode();
        using var replay = await client.Http.PostAsJsonAsync("/v2/session/" + client.Session, envelope); Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }
    [Fact] public async Task UnpairedLaunchAndDeniedPairingCannotExecute()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(false), launcher); await host.StartAsync();
        using var client = new Client(); await client.ConnectAsync();
        using var response = await client.Send(new() { Action = "confirm", Code = client.Code });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(0, launcher.Calls);
        using var expired = await client.Send(new() { Action = "launch", Launch = new() { Game = "spellborn", GamePath = "C:\\Games" } });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }
    [Fact] public async Task WrongPairingCodeIsRejected()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var client = new Client(); await client.ConnectAsync();
        using var response = await client.Send(new() { Action = "confirm", Code = "not-the-code" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(0, launcher.Calls);
    }
    [Fact] public async Task HostAndOriginAreEnforcedAndOccupiedPortFails()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{BridgeHost.Port}/v2/hello") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await http.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{BridgeHost.Port}/v2/hello") { Content = JsonContent.Create(new { }) };
        badHost.Headers.Host = "attacker.example"; badHost.Headers.Add("Origin", Origin);
        using var badResponse = await http.SendAsync(badHost); Assert.Equal(HttpStatusCode.Forbidden, badResponse.StatusCode);
        await using var other = new BridgeHost(trust, new Prompt(true), launcher);
        await Assert.ThrowsAnyAsync<IOException>(() => other.StartAsync());
    }
    [Fact] public async Task FailureDoesNotReportSuccessOrExposeException()
    {
        using var trust = new TrustStore(); var launcher = new Launcher { Fail = true };
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var client = new Client(); await client.ConnectAsync(); await client.Exchange(new() { Action = "confirm", Code = client.Code });
        var reply = await client.Exchange(new() { Action = "launch", Launch = new() { Game = "spellborn", GamePath = "C:\\Games" } });
        Assert.False(reply.Status.Success); Assert.True(reply.Status.Completed); Assert.DoesNotContain("synthetic-secret", reply.Status.Message);
    }
    [Fact] public async Task CancellationStopsWorkAndRevocationExpiresSession()
    {
        using var trust = new TrustStore(); var launcher = new Launcher { Block = true };
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var client = new Client(); await client.ConnectAsync(); await client.Exchange(new() { Action = "confirm", Code = client.Code });
        await client.Exchange(new() { Action = "launch", Launch = new() { Game = "spellborn", GamePath = "C:\\Games" } });
        await client.Exchange(new() { Action = "cancel" });
        BridgeReply reply;
        do { await Task.Delay(10); reply = await client.Exchange(new() { Action = "status" }); } while (!reply.Status.Completed);
        Assert.Equal("cancelled", reply.Status.State);
        await host.ForgetBrowsersAsync();
        using var response = await client.Send(new() { Action = "status" }); Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
    [Fact] public async Task ProductionJavaScriptInteroperatesWithDotNetAndRemembersPairing()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        var info = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "crypto-interop.mjs"));
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fixtures", "bridge.mjs"));
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error); Assert.Contains("interop passed", await output); Assert.Equal(1, launcher.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"identity\":null,\"ephemeral\":null,\"nonce\":null,\"signature\":null}")]
    public async Task MalformedHelloIsRejectedWithoutStartingWork(string json)
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var http = new HttpClient(); http.DefaultRequestHeaders.Add("Origin", Origin);
        using var response = await http.PostAsync($"http://127.0.0.1:{BridgeHost.Port}/v2/hello", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, launcher.Calls);
    }

    [Fact] public async Task CancellingPendingPairingExpiresSessionAndAllowsImmediateRetry()
    {
        using var trust = new TrustStore(); var launcher = new Launcher();
        await using var host = new BridgeHost(trust, new Prompt(true), launcher); await host.StartAsync();
        using var first = new Client(); await first.ConnectAsync();
        Assert.False((await first.Exchange(new() { Action = "disconnect" })).Paired);
        using var expired = await first.Send(new() { Action = "confirm", Code = first.Code });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        using var retry = new Client(); await retry.ConnectAsync();
        Assert.True((await retry.Exchange(new() { Action = "confirm", Code = retry.Code })).Paired);
        Assert.Equal(0, launcher.Calls);
    }

}
