using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlJobRunner;
using Xunit;

public class RequestTests
{
    private static Store NewStore() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["DataDirectory"]=Path.Combine(Path.GetTempPath(),"SqlJobRunnerTests",Guid.NewGuid().ToString("N")) }).Build());
    private sealed class FakeAgentStore : IRequestStore
    {
        public readonly DemoRequestStore Inner = new();
        public Dictionary<int, AgentStatus> Agent = new();
        public int StatusQueries;
        public Task<List<JobRequest>> List(CancellationToken ct) => Inner.List(ct);
        public Task<int> Create(JobDefinition job, string by, CancellationToken ct) => Inner.Create(job, by, ct);
        public Task<bool> Claim(int id, string token, ConnectionInput e, CancellationToken ct) => Inner.Claim(id, token, e, ct);
        public Task Release(int id, string token, ConnectionInput e, CancellationToken ct) => Inner.Release(id, token, e, ct);
        public Task<Dictionary<int, AgentStatus>> Statuses(IReadOnlyList<JobRequest> taken, CancellationToken ct)
        { StatusQueries += taken.Count; return Task.FromResult(taken.Where(t => Agent.ContainsKey(t.RequestId)).ToDictionary(t => t.RequestId, t => Agent[t.RequestId])); }
    }
    private static Task<int> NewRequest(IRequestStore s) => s.Create(new JobDefinition { JobName = "J", Sql1 = "SELECT 1" }, "CORP\\consultant", default);
    [Fact]
    public void Request_server_defaults_to_pasifik_and_is_saved_from_settings()
    {
        var store = NewStore();
        Assert.Equal(@"SRVDEV\PASIFIK", store.GetRequestSettings().Server);
        store.SetRequestSettings(new(" OTHER\\SQL ", Encrypt: true));
        Assert.Equal(new RequestSettings("OTHER\\SQL", true, true), store.GetRequestSettings());
        Assert.Null(store.GetConnection());
        Assert.Throws<ArgumentException>(() => store.SetRequestSettings(new("  ")));
    }
    [Fact]
    public void Embedded_credentials_decrypt()
    {
        Assert.False(string.IsNullOrWhiteSpace(RequestCredentials.UserName));
        Assert.False(string.IsNullOrWhiteSpace(RequestCredentials.Password));
    }
    [Fact]
    public async Task Only_one_user_can_claim_a_waiting_request()
    {
        var requests = new DemoRequestStore();
        var id = await NewRequest(requests);
        Assert.True(await requests.Claim(id, "tok_a", new("DEMO", IntegratedSecurity: false, UserName: "employee_a"), default));
        Assert.False(await requests.Claim(id, "tok_b", new("DEMO", IntegratedSecurity: false, UserName: "employee_b"), default));
        await requests.Release(id, "tok_b", new("DEMO"), default);
        Assert.Equal("employee_a", Assert.Single(await requests.List(default)).ExecutedBy);
        await requests.Release(id, "tok_a", new("DEMO"), default);
        var released = Assert.Single(await requests.List(default));
        Assert.Null(released.ExecutedBy); Assert.Null(released.RunToken);
    }
    [Fact]
    public void Claimed_token_matches_the_job_name_the_run_sends_to_the_generator()
    {
        var store = NewStore(); store.SetConnection(new("DEMO"));
        var job = new JobDefinition { JobName = "Some Job", Sql1 = "SELECT 1" };
        var requestId = Guid.NewGuid();
        var token = GeneratorContract.Token(new RunRecord { Id = requestId.ToString("N"), Definition = job });
        var run = store.Execute(job, requestId.ToString(), 7);
        Assert.Equal(token, GeneratorContract.Token(run));
        Assert.Equal(7, run.RequestId);
    }
    [Fact]
    public async Task Other_users_see_status_from_msdb_and_finished_jobs_are_not_queried_again()
    {
        var requests = new FakeAgentStore();
        var id = await NewRequest(requests);
        var monitor = new RequestMonitor(NewStore(), requests, NullLogger<RequestMonitor>.Instance);
        await monitor.Refresh(default);
        Assert.Equal("Pending", Assert.Single(monitor.Requests).Status);
        await requests.Claim(id, "tok", new("DEMO"), default);
        await monitor.Refresh(default);
        Assert.Equal("Starting", monitor.Requests[0].Status);
        requests.Agent[id] = new("Running", "izleniyor", "Son tamamlanan adım: 1. MIG", "BTVY_tok_x", null);
        await monitor.Refresh(default);
        Assert.Equal("Running", monitor.Requests[0].Status); Assert.Equal("Son tamamlanan adım: 1. MIG", monitor.Requests[0].CurrentStep);
        requests.Agent[id] = new("Succeeded", "done", null, "BTVY_tok_x", DateTimeOffset.Now);
        await monitor.Refresh(default);
        var queries = requests.StatusQueries;
        await monitor.Refresh(default);
        Assert.Equal("Succeeded", monitor.Requests[0].Status); Assert.Equal(queries, requests.StatusQueries);
    }
    [Fact]
    public async Task The_computer_running_the_request_uses_its_own_run()
    {
        var store = NewStore(); store.SetConnection(new("DEMO"));
        var requests = new FakeAgentStore();
        var id = await NewRequest(requests);
        var runGuid = Guid.NewGuid();
        var job = (await requests.List(default))[0].ToDefinition();
        await requests.Claim(id, GeneratorContract.Token(new RunRecord { Id = runGuid.ToString("N"), Definition = job }), new("DEMO"), default);
        store.Execute(job, runGuid.ToString(), id);
        var monitor = new RequestMonitor(store, requests, NullLogger<RequestMonitor>.Instance);
        await monitor.Refresh(default);
        Assert.Equal("Queued", monitor.Requests[0].Status); Assert.Equal(0, requests.StatusQueries);
    }
    [Fact]
    public void Missing_job_becomes_unknown_after_a_grace_period()
    {
        var row = new JobRequest { RequestId = 1, ExecutedBy = "CORP\\x", RunToken = "tok", ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        Assert.Equal("Unknown", RequestMonitor.Resolve(row, null, null).Status);
        Assert.Equal("Starting", RequestMonitor.Resolve(row with { ExecutedAt = DateTimeOffset.UtcNow }, null, null).Status);
    }
}
