using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlJobRunner;
using Xunit;

public class WorkerTests
{
    private static Store Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "SqlJobRunnerTests", Guid.NewGuid().ToString("N")) }).Build());
    private class FakeAgent : IAgentClient
    {
        public int Submissions;
        public int Starts;
        public bool Timeout;
        public bool StartTimeout;
        public bool CreationFailed;
        public bool MissingJob;
        public string PollStatus = "Running";
        public Task Prepare(RunRecord r, ConnectionInput c, CancellationToken ct) { r.ServerSubmittedAt = DateTime.Now; r.GeneratorJobName = GeneratorContract.Token(r); return Task.CompletedTask; }
        public Task Submit(RunRecord r, ConnectionInput c, CancellationToken ct) { Submissions++; if (Timeout) throw new TimeoutException("network"); return Task.CompletedTask; }
        public Task Poll(RunRecord r, ConnectionInput c, CancellationToken ct) { r.Status = PollStatus; r.CurrentStep = "2 (LRY)"; return Task.CompletedTask; }
        public Task<bool> Resolve(RunRecord r, ConnectionInput c, CancellationToken ct)
        {
            if (CreationFailed) { r.Status = "Failed"; r.Message = "Prosedür logu: oluşturma başarısız"; return Task.FromResult(false); }
            if (MissingJob) { r.Status = "Unknown"; return Task.FromResult(false); }
            r.AgentJobId = Guid.NewGuid().ToString(); r.AgentJobName = GeneratorContract.Prefix(r) + "20261016_120000"; return Task.FromResult(true);
        }
        public Task Start(RunRecord r, ConnectionInput c, CancellationToken ct) { Starts++; if (StartTimeout) throw new TimeoutException("Agent start timeout"); return Task.CompletedTask; }
    }
    private static RunRecord Queue(Store store)
    {
        var job = new JobDefinition { JobName = "test", AgentJobName = "agent" }; store.SaveJob(job); store.SetConnection(new("fake")); return store.Enqueue(job.Id);
    }
    [Fact]
    public async Task Procedure_return_is_not_success_until_agent_reports_completion()
    {
        var store = Store(); var run = Queue(store); var agent = new FakeAgent(); var worker = new JobWorker(store,agent,NullLogger<JobWorker>.Instance);
        await worker.Process(run,default); Assert.Equal("Running",Assert.Single(store.Runs()).Status); Assert.Empty(store.Notifications());
        agent.PollStatus = "Succeeded"; await worker.Process(Assert.Single(store.Runs()),default);
        Assert.Equal(1,agent.Submissions); Assert.Equal(1,agent.Starts); Assert.Equal("Succeeded",Assert.Single(store.Runs()).Status); Assert.Single(store.Notifications());
    }
    [Fact]
    public async Task Ambiguous_submission_is_monitored_after_restart_without_resubmitting()
    {
        var store = Store(); var run = Queue(store); var agent = new FakeAgent { Timeout = true }; var worker = new JobWorker(store,agent,NullLogger<JobWorker>.Instance);
        await worker.Process(run,default); Assert.Equal("Unknown",Assert.Single(store.Runs()).Status);
        var recoveredWorker = new JobWorker(store,agent,NullLogger<JobWorker>.Instance);
        await recoveredWorker.Process(Assert.Single(store.Runs()),default);
        Assert.Equal(1,agent.Submissions); Assert.Equal(1,agent.Starts); Assert.Equal("Running",Assert.Single(store.Runs()).Status);
    }
    [Fact]
    public async Task Ambiguous_agent_start_is_not_sent_twice()
    {
        var store = Store(); var run = Queue(store); var agent = new FakeAgent { StartTimeout = true }; var worker = new JobWorker(store,agent,NullLogger<JobWorker>.Instance);
        await worker.Process(run,default); var saved = Assert.Single(store.Runs()); Assert.Equal("Unknown",saved.Status); Assert.True(saved.StartRequested); Assert.NotNull(saved.AgentJobId);
        await new JobWorker(store,agent,NullLogger<JobWorker>.Instance).Process(saved,default);
        Assert.Equal(1,agent.Starts); Assert.Equal(1,agent.Submissions);
    }
    [Fact]
    public async Task Swallowed_procedure_failure_is_reported_and_never_started()
    {
        var store = Store(); var run = Queue(store); var agent = new FakeAgent { CreationFailed = true };
        await new JobWorker(store,agent,NullLogger<JobWorker>.Instance).Process(run,default);
        Assert.Equal("Failed",Assert.Single(store.Runs()).Status); Assert.Equal(0,agent.Starts); Assert.Single(store.Notifications());
    }
    [Fact]
    public async Task Missing_created_job_is_not_started_or_regenerated()
    {
        var store = Store(); var run = Queue(store); var agent = new FakeAgent { MissingJob = true }; var worker = new JobWorker(store,agent,NullLogger<JobWorker>.Instance);
        await worker.Process(run,default); await worker.Process(Assert.Single(store.Runs()),default);
        Assert.Equal("Unknown",Assert.Single(store.Runs()).Status); Assert.Equal(0,agent.Starts); Assert.Equal(1,agent.Submissions);
    }
    [Fact]
    public void Closing_unknown_tracking_cannot_be_overwritten_by_inflight_poll()
    {
        var store = Store(); var run = Queue(store); run.Status = "Unknown"; store.SaveRun(run); store.CloseTracking(run.Id);
        run.Status = "Running"; store.SaveRun(run); Assert.Equal("Unverified",Assert.Single(store.Runs()).Status);
        run.Status = "Succeeded"; store.Complete(run); Assert.Equal("Unverified",Assert.Single(store.Runs()).Status);
    }
}
