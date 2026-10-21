using Xunit;

namespace SqlJobRunner.Tests;

public class DemoAgentTests
{
    [Theory]
    [InlineData("DEMO_SUCCESS", 20, "Succeeded")]
    [InlineData("DEMO_FAILURE", 15, "Failed")]
    [InlineData("DEMO_CANCEL", 15, "Canceled")]
    [InlineData("DEMO_UNKNOWN", 100, "Unknown")]
    [InlineData("DEMO_SLOW", 30, "Running")]
    [InlineData("DEMO_SLOW", 65, "Succeeded")]
    [InlineData("DEMO_RETRY", 15, "Running")]
    [InlineData("DEMO_RETRY", 35, "Succeeded")]
    public async Task SimulatesAgentStates(string name, int seconds, string expected)
    {
        var run = new RunRecord { Definition = new JobDefinition { JobName = name, Sql1 = "SELECT 1" }, ServerSubmittedAt = DateTime.UtcNow.AddSeconds(-seconds) };
        await new DemoAgentClient().Poll(run, new ConnectionInput(), default);
        Assert.Equal(expected, run.Status);
        if (name == "DEMO_RETRY" && expected == "Running") Assert.Contains("\"status\":2", run.StepsJson);
    }
    [Fact]
    public async Task GeneratorFailureNeverResolvesAJob()
    {
        var run = new RunRecord { Definition = new JobDefinition { JobName = "DEMO_GENERATOR_ERROR" } };
        Assert.False(await new DemoAgentClient().Resolve(run, new ConnectionInput(), default));
        Assert.Null(run.AgentJobId);
        Assert.Equal("Failed", run.Status);
    }
    [Fact]
    public void ConnectionFailureIsDeterministic() => Assert.Throws<InvalidOperationException>(() => DemoAgentClient.Test(new ConnectionInput(Server: "DEMO-FAIL")));
}
