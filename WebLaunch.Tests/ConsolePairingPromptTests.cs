using System.Threading.Channels;
using WebLaunch.Bridge;
using Xunit;

namespace WebLaunch.Tests;

public sealed class ConsolePairingPromptTests
{
    private sealed class BlockingReader : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public override string? ReadLine() => lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        public bool WriteLine(string line) => lines.Writer.TryWrite(line);
    }

    private sealed class PromptWriter : StringWriter
    {
        public TaskCompletionSource PromptDisplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            PromptDisplayed.TrySetResult();
        }
    }

    [Fact]
    public async Task TimedOutPairingDoesNotPoisonTheNextConsoleApproval()
    {
        var input = new BlockingReader();
        using var output = new PromptWriter();
        var prompt = new ConsolePairingPrompt(input, output);

        using var firstCancellation = new CancellationTokenSource();
        var first = prompt.ConfirmAsync("https://first.example", "111111", firstCancellation.Token);
        await output.PromptDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstCancellation.Cancel();
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5)));

        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var second = prompt.ConfirmAsync("https://second.example", "222222", secondCancellation.Token);
        Assert.True(input.WriteLine("222222"));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QuietConsolePromptNeverReadsOrApprovesNewPairings()
    {
        var input = new BlockingReader();
        using var output = new PromptWriter();
        var prompt = new ConsolePairingPrompt(input, output, quiet: true);

        Assert.False(await prompt.ConfirmAsync("https://browser.example", "123456", CancellationToken.None));
        Assert.False(output.ToString().Contains("Pair https://", StringComparison.Ordinal));
    }
}
