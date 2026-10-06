using WebLaunch.Core;
using Xunit;
namespace WebLaunch.Tests;

public sealed class BootstrapTests
{
    [Theory]
    [InlineData("HandleWebRequest:connect?v=2", "gui")]
    [InlineData("handlewebrequest:connect?v=2&mode=gui", "gui")]
    [InlineData("HandleWebRequest:connect?mode=console&v=2", "console")]
    public void BootstrapSelectsOnlySupportedDesktopModes(string link, string mode)
    {
        Assert.True(BootstrapRequest.TryParse(link, out var request)); Assert.Equal(mode, request!.Mode);
    }
    [Theory]
    [InlineData("v=2&mode=console&mode=gui")][InlineData("v=2&v=2")]
    [InlineData("v=2&mode=quiet")][InlineData("v=1&mode=gui")]
    [InlineData("v=2&mode=console%20--install")][InlineData("v=2&password=secret")]
    [InlineData("v=2&mode=gui\n")][InlineData("v=2&mode=GUI")]
    public void BootstrapRejectsDuplicatesUnknownFieldsAndArgumentInjection(string query) =>
        Assert.Throws<ArgumentException>(() => BootstrapRequest.TryParse("HandleWebRequest:connect?" + query, out _));
}
