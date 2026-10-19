using LaunchApp.Shared;
using Xunit;

namespace WebLaunch.Tests;

public sealed class BrowserDiagnosticTextTests
{
    [Theory]
    [InlineData("JavaScript interop error: Authorization: Bearer abc.DEF-123_xyz failed", "Authorization=[redacted] failed", "abc.DEF-123_xyz")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization=[redacted]", "dXNlcjpwYXNz")]
    [InlineData("password=\"p\\\"ss word\" retry", "password=[redacted] retry", "ss word")]
    [InlineData("secret='a\\'b c' retry", "secret=[redacted] retry", "b c")]
    [InlineData("{\"token\":\"abc\\\"def ghi\"}", "{\"token\"=[redacted]}", "def")]
    [InlineData("password=\"abc def", "password=[redacted]", "def")]
    [InlineData("token=abc123&next=1", "token=[redacted]&next=1", "abc123")]
    public void SensitiveValuesAreFullyRedacted(string message, string expected, string secret)
    {
        var summary = BrowserDiagnosticText.SafeSummary(message);

        Assert.DoesNotContain(secret, summary);
        Assert.DoesNotContain("ghi", summary);
        Assert.Equal(expected, summary);
    }

    [Theory]
    [InlineData("Failed https://example.test/a?x=1 now", "Failed [url redacted] now")]
    [InlineData(@"Failed C:\Users\example\file.txt now", "Failed [path redacted] now")]
    [InlineData("request body: {...}", "request/response payload=[redacted]")]
    [InlineData("Nothing sensitive here", "Nothing sensitive here")]
    [InlineData("JSException: Transport failed\nignored", "Transport failed")]
    [InlineData("Error: Transport  failed", "Transport failed")]
    [InlineData("", null)]
    [InlineData(" \t\r\n ", null)]
    public void ExistingSummaryBehaviorIsPreserved(string message, string? expected)
    {
        Assert.Equal(expected, BrowserDiagnosticText.SafeSummary(message));
    }

    [Fact]
    public void LongSummaryIsTruncatedAt180Characters()
    {
        Assert.Equal(new string('x', 180) + "…", BrowserDiagnosticText.SafeSummary(new string('x', 181)));
    }
}
