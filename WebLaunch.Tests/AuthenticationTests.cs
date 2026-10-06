using System.Net;
using CoreLibLaunchSupport;
using Xunit;
namespace WebLaunch.Tests;

public sealed class AuthenticationTests
{
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    [Theory][InlineData(false, null)][InlineData(true, "123456")]
    public async Task SignInPreservesSteamAndOptionalOtpWithoutPuttingCredentialsInUrl(bool steam, string? otp)
    {
        using var http = new HttpClient(new Transport(async (request, token) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.DoesNotContain("synthetic-secret", request.RequestUri.ToString());
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("issteam=" + (steam ? 1 : 0), request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent("\t<input type=\"hidden\" name=\"_STORED_\" value=\"fixture\">") };
            }
            Assert.Contains("issteam=" + (steam ? 1 : 0), request.Headers.Referrer!.Query);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("password=synthetic-secret", body);
            Assert.Contains("otppw=" + (otp ?? ""), body);
            return new(HttpStatusCode.OK) { Content = new StringContent("sid,synthetic-session,terms") };
        }));
        var service = new FfxivAuthenticationService(http, "synthetic-agent");
        var stored = await service.FetchStoredValueAsync(steam, default);
        Assert.Equal("synthetic-session", await service.RequestSessionIdAsync(new("synthetic-user", "synthetic-secret", otp, steam), stored, default));
    }
    [Fact] public async Task HttpFailureCannotBeMistakenForSuccessfulAuthentication()
    {
        using var http = new HttpClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("sid,do-not-accept,terms") })));
        var service = new FfxivAuthenticationService(http, "test");
        await Assert.ThrowsAsync<HttpRequestException>(() => service.RequestSessionIdAsync(new("u", "p", null, false), "test", default));
    }
    [Fact] public async Task AuthenticationCancellationReachesTransport()
    {
        using var http = new HttpClient(new Transport(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FfxivAuthenticationService(http, "test").FetchStoredValueAsync(false, cancellation.Token));
    }
}
