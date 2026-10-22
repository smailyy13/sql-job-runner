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
        public Dictionary<string, AgentStatus> Agent = new();
        public int StatusQueries;
        public ConnectionInput? FinishConnection;
        public Task<List<JobRequest>> List(CancellationToken ct) => Inner.List(ct);
        public Task<int> Create(JobDefinition job, string by, CancellationToken ct) => Inner.Create(job, by, ct);
        public Task<ClaimResult> Claim(int id, string token, string? previous, ConnectionInput e, CancellationToken ct) => Inner.Claim(id, token, previous, e, ct);
        public Task SaveFinishedAt(int id, string token, DateTimeOffset time, ConnectionInput employee, CancellationToken ct) { FinishConnection = employee; return Inner.SaveFinishedAt(id, token, time, employee, ct); }
        public Task Release(int id, string token, ConnectionInput e, CancellationToken ct) => Inner.Release(id, token, e, ct);
        public Task<Dictionary<int, AgentStatus>> Statuses(IReadOnlyList<JobRequest> taken, CancellationToken ct)
        { StatusQueries += taken.Count; return Task.FromResult(taken.Where(t => Agent.ContainsKey(t.RunToken!)).ToDictionary(t => t.RequestId, t => Agent[t.RunToken!])); }
    }
    private static Task<int> NewRequest(IRequestStore s) => s.Create(new JobDefinition { JobName = "J", Sql1 = "SELECT 1" }, "CORP\\consultant", default);
    [Fact]
    public async Task Finish_time_is_saved_and_stale_attempt_cannot_overwrite_retry()
    {
        var requests = new FakeAgentStore(); var id = await NewRequest(requests);
        var employee = new ConnectionInput("DEMO", UserName: "runner", IntegratedSecurity: false, Password: "test-only");
        var store = NewStore(); store.SetConnection(employee);
        var run = store.Execute((await requests.List(default))[0].ToDefinition(), Guid.NewGuid().ToString(), id);
        var token = GeneratorContract.Token(run);
        await requests.Claim(id, token, null, employee, default);
        var end = DateTimeOffset.UtcNow;
        run.Status = "Succeeded"; run.AgentStartedAt = end.AddSeconds(-10); run.AgentFinishedAt = end;
        store.SaveRun(run);
        store.SetConnection(new("OTHER"));
        var monitor = new RequestMonitor(store, requests, NullLogger<RequestMonitor>.Instance);
        await monitor.Refresh(default);
        Assert.Equal(end, (await requests.List(default))[0].FinishedAt);
        Assert.Equal("runner", requests.FinishConnection!.UserName);
        Assert.Equal("DEMO", requests.FinishConnection.Server);
        await requests.Claim(id, "second", token, employee, default);
        await requests.SaveFinishedAt(id, token, end, employee, default);
        Assert.Null((await requests.List(default))[0].FinishedAt);
    }
    [Fact]
    public async Task Observer_does_not_write_finish_time_using_shared_login()
    {
        var requests = new FakeAgentStore(); var id = await NewRequest(requests);
        await requests.Claim(id, "remote", null, new("DEMO"), default);
        requests.Agent["remote"] = new("Succeeded", null, null, "job", DateTimeOffset.UtcNow);
        var monitor = new RequestMonitor(NewStore(), requests, NullLogger<RequestMonitor>.Instance);
        await monitor.Refresh(default);
        Assert.Null(requests.FinishConnection);
        Assert.Null((await requests.List(default))[0].FinishedAt);
    }

    [Fact]
    public void Request_times_use_agent_times_instead_of_claim_or_observation_time()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        var end = start.AddSeconds(15);
        var row = new JobRequest { ExecutedBy = "employee", ExecutedAt = start.AddSeconds(-30) };
        var local = new RunRecord { Status = "Succeeded", AgentStartedAt = start, AgentFinishedAt = end, FinishedUtc = end.AddSeconds(3) };
        var resolved = RequestMonitor.Resolve(row, local, null);
        Assert.Equal(start, resolved.StartedAt); Assert.Equal(end, resolved.FinishedAt);
        var remote = RequestMonitor.Resolve(row, null, new AgentStatus("Succeeded", null, null, "job", end, start));
        Assert.Equal(resolved.StartedAt, remote.StartedAt); Assert.Equal(resolved.FinishedAt, remote.FinishedAt);
        Assert.Null(RequestMonitor.Resolve(row, local with { AgentStartedAt = null, AgentFinishedAt = null }, null).FinishedAt);
    }
    [Fact]
    public void Different_requests_with_same_name_run_concurrently_but_same_request_is_blocked()
    {
        var store = NewStore(); store.SetConnection(new("DEMO"));
        var job = new JobDefinition { JobName = "Employee", Sql1 = "SELECT 1" };
        var first = store.Execute(job, Guid.NewGuid().ToString(), 101);
        var second = store.Execute(job with { Id = Guid.NewGuid().ToString("N") }, Guid.NewGuid().ToString(), 102);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Throws<InvalidOperationException>(() => store.Execute(job with { Id = Guid.NewGuid().ToString("N") }, Guid.NewGuid().ToString(), 101));
    }
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
        Assert.Equal(ClaimResult.Claimed, await requests.Claim(id, "tok_a", null, new("DEMO", IntegratedSecurity: false, UserName: "employee_a"), default));
        Assert.Equal(ClaimResult.Taken, await requests.Claim(id, "tok_b", null, new("DEMO", IntegratedSecurity: false, UserName: "employee_b"), default));
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
        await requests.Claim(id, "tok", null, new("DEMO"), default);
        await monitor.Refresh(default);
        Assert.Equal("Starting", monitor.Requests[0].Status);
        requests.Agent["tok"] = new("Running", "izleniyor", "Son tamamlanan adım: 1. MIG", "BTVY_tok_x", null);
        await monitor.Refresh(default);
        Assert.Equal("Running", monitor.Requests[0].Status); Assert.Equal("Son tamamlanan adım: 1. MIG", monitor.Requests[0].CurrentStep);
        requests.Agent["tok"] = new("Succeeded", "done", null, "BTVY_tok_x", DateTimeOffset.Now);
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
        await requests.Claim(id, GeneratorContract.Token(new RunRecord { Id = runGuid.ToString("N"), Definition = job }), null, new("DEMO"), default);
        store.Execute(job, runGuid.ToString(), id);
        var monitor = new RequestMonitor(store, requests, NullLogger<RequestMonitor>.Instance);
        await monitor.Refresh(default);
        Assert.Equal("Queued", monitor.Requests[0].Status); Assert.Equal(0, requests.StatusQueries);
    }
    [Fact]
    public async Task Failed_request_can_run_again_but_only_once_and_shows_the_new_attempt()
    {
        var requests = new FakeAgentStore();
        var id = await NewRequest(requests);
        var monitor = new RequestMonitor(NewStore(), requests, NullLogger<RequestMonitor>.Instance);
        await requests.Claim(id, "first", null, new("DEMO"), default);
        requests.Agent["first"] = new("Failed", "step 2 failed", null, "BTVY_first_x", DateTimeOffset.Now);
        await monitor.Refresh(default);
        Assert.True(RequestStates.Rerunnable(monitor.Requests[0].Status));
        // Two employees retry the same failed attempt: only the first one takes it.
        Assert.Equal(ClaimResult.Claimed, await requests.Claim(id, "second", "first", new("DEMO"), default));
        Assert.Equal(ClaimResult.Taken, await requests.Claim(id, "third", "first", new("DEMO"), default));
        Assert.Equal(ClaimResult.Taken, await requests.Claim(id, "third", null, new("DEMO"), default));
        await monitor.Refresh(default);
        Assert.Equal("Starting", monitor.Requests[0].Status);
        requests.Agent["second"] = new("Running", "izleniyor", null, "BTVY_second_x", null);
        await monitor.Refresh(default);
        Assert.Equal("Running", monitor.Requests[0].Status);
        Assert.False(RequestStates.Rerunnable("Running")); Assert.False(RequestStates.Rerunnable("Unknown"));
    }
    [Fact]
    public void Missing_job_becomes_unknown_after_a_grace_period()
    {
        var row = new JobRequest { RequestId = 1, ExecutedBy = "CORP\\x", RunToken = "tok", ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        Assert.Equal("Unknown", RequestMonitor.Resolve(row, null, null).Status);
        Assert.Equal("Starting", RequestMonitor.Resolve(row with { ExecutedAt = DateTimeOffset.UtcNow }, null, null).Status);
    }
}
