using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class StepLimitTests
{
    [Test]
    public async Task DefaultStepLimit_AllowsMoreThanFortyStepsAndPreservesCounts()
    {
        var client = ScriptedClient();
        var loop = new AgentLoop(client, new ToolRegistry([new FakeTool("loop")]),
                                 Options(new TinyHarnessConfig().MaxAgentSteps));

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("done"));
        Assert.That(result.Steps, Is.EqualTo(46));
        Assert.That(result.ToolExecutions, Is.EqualTo(45));
        Assert.That(client.Requests, Is.EqualTo(46));
    }

    [Test]
    public async Task UnlimitedSteps_StillHonorsCancellationBeyondFormerLimit()
    {
        using var cancellation = new CancellationTokenSource();
        var       client       = ScriptedClient();
        client.CancelMidStream = () =>
        {
            if (client.Requests == 42) cancellation.Cancel();
        };
        var loop = new AgentLoop(client, new ToolRegistry([new FakeTool("loop")]), Options(0));

        var result = await loop.RunAsync("sys", "go", cancellation.Token);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(client.Requests, Is.EqualTo(42));
        Assert.That(result.ToolExecutions, Is.EqualTo(41));
    }

    [Test]
    public async Task UnlimitedSteps_StillHonorsExplicitRequestBudget()
    {
        var client  = ScriptedClient();
        var options = Options(0) with { Budget = new RunBudgetLimits { MaxModelRequests = 2 } };
        var loop    = new AgentLoop(client, new ToolRegistry([new FakeTool("loop")]), options);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.Error, Does.Contain("model-requests"));
        Assert.That(client.Requests, Is.EqualTo(2));
        Assert.That(result.ToolExecutions, Is.EqualTo(2));
    }

    [Test]
    public void NegativeSteps_AreRejectedAtRuntimeComposition()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                       new AgentLoop(new FakeChatClient(), new ToolRegistry([]),
                                                                     Options(-1)));
    }

    [TestCase(0)]
    [TestCase(3)]
    public async Task StepSetting_RoundTripsForUserConfigAndExplicitFile(int steps)
    {
        using var directory = new TestTempDir();
        var       userPath  = Path.Combine(directory.Root, "user-config.json");
        await UserConfigStore.SaveAsync(userPath, new UserConfig
        {
            Settings = new UserConfigSettings { MaxAgentSteps = steps }
        }, CancellationToken.None);
        var user = await ConfigResolver.ResolveAsync(null, CancellationToken.None, userPath);
        Assert.That(user.Config.MaxAgentSteps, Is.EqualTo(steps));

        var explicitPath   = directory.WriteFile("explicit.json", $"{{\"maxAgentSteps\":{steps}}}");
        var explicitConfig = await ConfigurationLoader.LoadAsync(explicitPath, CancellationToken.None);
        Assert.That(explicitConfig.MaxAgentSteps, Is.EqualTo(steps));
        var client = ScriptedClient();
        var loop = new AgentLoop(client, new ToolRegistry([new FakeTool("loop")]),
                                 Options(explicitConfig.MaxAgentSteps));
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo(steps   == 0 ? AgentStatus.Completed : AgentStatus.StepLimitReached));
        Assert.That(client.Requests, Is.EqualTo(steps == 0 ? 46 : steps));
    }

    [Test]
    public async Task NegativeStepSettings_AreRejectedByUserConfigAndExplicitFiles()
    {
        using var directory = new TestTempDir();
        var       userPath  = directory.WriteFile("user-config.json", "{\"settings\":{\"maxAgentSteps\":-1}}");
        var userError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                           UserConfigStore.LoadAsync(userPath,
                                                                                    CancellationToken.None));
        Assert.That(userError!.Message, Does.Contain("maxAgentSteps"));
        var explicitPath = directory.WriteFile("explicit.json", "{\"maxAgentSteps\":-1}");
        var explicitError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                               ConfigurationLoader
                                                                                  .LoadAsync(explicitPath,
                                                                                            CancellationToken.None));
        Assert.That(explicitError!.Message, Does.Contain("maxAgentSteps"));
    }

    private static AgentOptions Options(int steps) => new()
    {
        Model = "test-model", MaxAgentSteps = steps, DefaultToolTimeoutSeconds = 30
    };

    private static FakeChatClient ScriptedClient()
    {
        var client = new FakeChatClient();
        for (var index = 0; index < 45; index++)
            client.Enqueue(FakeChatClient.ToolCall("loop", "{}", $"call_{index}"));
        client.Enqueue(FakeChatClient.Text("done"));
        return client;
    }
}
