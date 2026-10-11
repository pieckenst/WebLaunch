using System.Security.Cryptography;
using System.Text;
using WebLaunch.Core;
using Xunit;

namespace WebLaunch.Tests;

public sealed class ProtocolTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
    private static string Link(string password = "p:+?=é🔐", string path = @"C:\Games\ファイナル Fantasy")
    {
        var data = Encoding.UTF8.GetBytes(password);
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var key = Encoding.UTF8.GetBytes(hash[..16]);
        for (var i = 0; i < data.Length; i++) data[i] ^= key[i % key.Length];
        return $"HandleWebRequest:HandleReqLaunch?ffxivhandle=yes?login={Uri.EscapeDataString("user+é")}:?pass={Uri.EscapeDataString(Convert.ToBase64String(data))}:?hash={hash}:?otp=:?gamepath={Uri.EscapeDataString(path)}:?issteam=yes";
    }
    [Theory]
    [InlineData("a")][InlineData("ab")][InlineData("abc")][InlineData("p:+?=é🔐")]
    public void LegacyRoundTripPreservesPaddingUnicodeAndDriveColon(string password)
    {
        var request = LegacyProtocol.Parse(Link(password), Now.AddHours(1), Now);
        Assert.Equal(password, request.Password); Assert.Equal("user+é", request.Username);
        Assert.Equal(@"C:\Games\ファイナル Fantasy", request.GamePath); Assert.True(request.IsSteam); Assert.Empty(request.Otp);
    }
    [Fact] public void LegacyIsDisabledWithoutLocalConsent() => Assert.Throws<InvalidOperationException>(() => LegacyProtocol.Parse(Link(), null, Now));
    [Fact] public void ExpiredAndUnboundedConsentFail()
    {
        Assert.Throws<InvalidOperationException>(() => LegacyProtocol.Parse(Link(), Now.AddSeconds(-1), Now));
        Assert.Throws<InvalidOperationException>(() => LegacyProtocol.Parse(Link(), Now.AddHours(25), Now));
    }
    [Theory]
    [InlineData(":?issteam=no")][InlineData(":?shell=calc")][InlineData(":?spellbornhandle=yes")][InlineData(":?unknown=%QQ")]
    public void DuplicateUnknownAmbiguousAndBadEncodingFail(string extra) => Assert.Throws<ArgumentException>(() => LegacyProtocol.Parse(Link() + extra, Now.AddHours(1), Now));
    [Fact] public void OversizedAndMalformedUrlsFail()
    {
        Assert.Throws<ArgumentException>(() => LegacyProtocol.Parse(Link() + new string('a', 8192), Now.AddHours(1), Now));
        Assert.Throws<ArgumentException>(() => LegacyProtocol.Parse("https://example.com/?ffxivhandle=yes", Now.AddHours(1), Now));
    }
    [Theory][InlineData("")][InlineData("123456")]
    public void OtpCanBeOptional(string otp) => new LaunchRequest { GamePath = @"C:\Games", Username = "u", Password = "p", Otp = otp }.Validate();
    [Fact] public void CredentialModelDoesNotPrintCredentials()
    {
        var request = new LaunchRequest { Password = "synthetic-secret", Otp = "123456" };
        Assert.DoesNotContain("synthetic-secret", request.ToString()); Assert.DoesNotContain("123456", request.ToString());
        request.ClearCredentials(); Assert.Empty(request.Password); Assert.Empty(request.Otp);
    }
    [Fact] public void ChannelAuthenticatesDirectionAndSequence()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        using var browser = new SecureChannel(secret, "test-transcript", false);
        using var desktop = new SecureChannel(secret, "test-transcript", true);
        var message = browser.Encrypt(new { Value = "synthetic-secret" });
        Assert.DoesNotContain("synthetic-secret", message.Ciphertext);
        Assert.Equal("synthetic-secret", desktop.Decrypt<System.Text.Json.JsonElement>(message).GetProperty("value").GetString());
        Assert.Throws<CryptographicException>(() => desktop.Decrypt<object>(message));
        var reply = desktop.Encrypt("accepted"); Assert.Equal("accepted", browser.Decrypt<string>(reply));
    }
    [Fact] public void TamperingAndWrongTranscriptAreRejected()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        using var browser = new SecureChannel(secret, "one", false);
        using var desktop = new SecureChannel(secret, "one", true);
        using var other = new SecureChannel(secret, "two", true);
        var message = browser.Encrypt("hello");
        Assert.ThrowsAny<CryptographicException>(() => other.Decrypt<string>(message));
        var bytes = Convert.FromBase64String(message.Ciphertext); bytes[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => desktop.Decrypt<string>(message with { Ciphertext = Convert.ToBase64String(bytes) }));
        Assert.Equal("hello", desktop.Decrypt<string>(message));
    }
    [Fact] public void HelloProofBindsOriginAndIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new ClientHello(2, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "");
        hello = hello with { Signature = BridgeCrypto.Sign(key, BridgeCrypto.ClientTranscript("https://pieckenst.github.io", hello)) };
        BridgeCrypto.ValidateHello("https://pieckenst.github.io", hello);
        Assert.Throws<CryptographicException>(() => BridgeCrypto.ValidateHello("https://evil.example", hello));
    }
}
