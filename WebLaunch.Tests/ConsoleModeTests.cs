using System.Collections.Concurrent;
using WebLaunch.Bridge;
using Xunit;

namespace WebLaunch.Tests;

public sealed class ConsoleModeTests
{
    [Theory]
    [InlineData("123456", true)]
    [InlineData("yes", false)]
    [InlineData("654321", false)]
    public async Task ConsoleRequiresMatchingCode(string input, bool accepted)
    {
        using var output = new StringWriter();
        using var reader = new BlockingReader();
        var prompt = new ConsolePairingPrompt(reader, output);
        var confirmation = prompt.ConfirmAsync("https://pieckenst.github.io", "123456", default);

        Assert.Contains("type the matching six digits", output.ToString());
        reader.Submit(input);

        Assert.Equal(accepted, await confirmation);
    }

    [Fact]
    public async Task QuietModeDeclinesNewPairingWithoutOutput()
    {
        using var output = new StringWriter();
        Assert.False(await new ConsolePairingPrompt(new StringReader("123456"), output, true).ConfirmAsync("https://pieckenst.github.io", "123456", default));
        Assert.Empty(output.ToString());
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly BlockingCollection<string?> lines = new();

        public void Submit(string? value) => lines.Add(value);

        public override string? ReadLine() => lines.Take();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lines.CompleteAdding();
                lines.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
