using WebLaunch.Bridge;
using Xunit;
namespace WebLaunch.Tests;

public sealed class ConsoleModeTests
{
    [Theory][InlineData("123456", true)][InlineData("yes", false)][InlineData("654321", false)]
    public async Task ConsoleRequiresMatchingCode(string input, bool accepted)
    {
        using var output = new StringWriter();
        var prompt = new ConsolePairingPrompt(new StringReader(input), output);
        Assert.Equal(accepted, await prompt.ConfirmAsync("https://pieckenst.github.io", "123456", default));
        Assert.Contains("123456", output.ToString());
    }
    [Fact] public async Task QuietModeDeclinesNewPairingWithoutOutput()
    {
        using var output = new StringWriter();
        Assert.False(await new ConsolePairingPrompt(new StringReader("123456"), output, true).ConfirmAsync("https://pieckenst.github.io", "123456", default));
        Assert.Empty(output.ToString());
    }
}
