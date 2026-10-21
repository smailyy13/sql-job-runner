using Microsoft.Extensions.Configuration;
using SqlJobRunner;
using Xunit;

public class PersistenceTests
{
    private static Store NewStore(string? directory = null) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["DataDirectory"] = directory ?? Path.Combine(Path.GetTempPath(), "SqlJobRunnerTests", Guid.NewGuid().ToString("N")) }).Build());
    private static JobDefinition Job() => new() { JobName = "sample", AgentJobName = "agent_sample", Sql2 = "UPDATE sample SET value='x'" };
    [Fact]
    public void Duplicate_submission_and_edits_are_blocked_while_unknown()
    {
        var store = NewStore(); var job = Job(); store.SaveJob(job); store.SetConnection(new("sql-a"));
        var run = store.Enqueue(job.Id); run.Status = "Unknown"; store.SaveRun(run);
        Assert.Throws<InvalidOperationException>(() => store.Enqueue(job.Id));
        Assert.Throws<InvalidOperationException>(() => store.SaveJob(job with { Sql2 = "different" }));
        Assert.Throws<InvalidOperationException>(() => store.DeleteJob(job.Id));
    }
    [Fact]
    public void Connection_snapshot_and_history_survive_reopen_and_definition_delete()
    {
        var store = NewStore(); var job = Job(); store.SaveJob(job); store.SetConnection(new("original-server"));
        var run = store.Enqueue(job.Id); store.SetConnection(new("different-server"));
        run.Status = "Succeeded"; store.Complete(run); store.DeleteJob(job.Id);
        var reopened = NewStore(store.DataDirectory); var saved = Assert.Single(reopened.Runs());
        Assert.Contains("original-server", saved.ConnectionSnapshot);
        Assert.Equal(job.Sql2, saved.Definition.Sql2);
        Assert.Empty(reopened.Jobs()); Assert.Single(reopened.Notifications()); Assert.NotNull(saved.FinishedUtc);
    }
    [Fact]
    public void Notifications_are_deduplicated_and_read_state_is_persistent()
    {
        var store = NewStore(); var job = Job(); store.SaveJob(job); store.SetConnection(new("server")); var run = store.Enqueue(job.Id);
        run.Status = "Failed"; store.Complete(run); store.Complete(run);
        Assert.Single(store.Notifications()); store.ReadNotifications(); Assert.True(Assert.Single(NewStore(store.DataDirectory).Notifications()).Read);
    }
    [Fact]
    public void Notification_preferences_are_respected()
    {
        var store = NewStore(); var job = Job() with { NotifySuccess = false }; store.SaveJob(job); store.SetConnection(new("server"));
        var run = store.Enqueue(job.Id); run.Status = "Succeeded"; store.Complete(run); Assert.Empty(store.Notifications());
    }
    [Fact]
    public void Same_logical_job_cannot_be_registered_twice()
    {
        var store = NewStore(); var job = Job(); store.SaveJob(job);
        Assert.Throws<InvalidOperationException>(() => store.SaveJob(Job() with { JobName = job.JobName.ToUpperInvariant() }));
    }
    [Fact]
    public void Password_is_protected_and_not_reused_for_different_server()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = NewStore(); store.SetConnection(new("sql-a",false,"user","test-secret"));
        var saved = store.GetConnection()!; Assert.DoesNotContain("test-secret",saved.Password!); Assert.Equal("test-secret",Store.Unprotect(saved.Password));
        store.SetConnection(new("sql-a",false,"user")); Assert.Equal(saved.Password,store.GetConnection()!.Password);
        store.SetConnection(new("sql-b",false,"user")); Assert.Null(store.GetConnection()!.Password);
    }
    [Fact]
    public void Unremembered_password_is_session_only_including_run_snapshots()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = NewStore(); var job = Job(); store.SaveJob(job);
        store.SetConnection(new("sql",false,"user","session-secret",RememberPassword:false));
        var connection = store.GetConnection()!; Assert.Null(connection.Password); Assert.Equal("session-secret",store.PasswordFor(connection));
        var run = store.Enqueue(job.Id); Assert.DoesNotContain("session-secret",run.ConnectionSnapshot!);
        var reopened = NewStore(store.DataDirectory); Assert.False(reopened.View(reopened.GetConnection()!).HasPassword);
        Assert.Throws<InvalidOperationException>(() => reopened.PasswordFor(reopened.GetConnection()!));
    }
    [Fact]
    public void Recent_connections_do_not_expose_passwords_and_can_be_forgotten()
    {
        var store = NewStore(); store.SetConnection(new("sql-a",Database:"EDWUTIL")); store.SetConnection(new("sql-b",Database:"msdb"));
        Assert.Equal(2,store.RecentConnections().Count);
        var recent = store.RecentConnections().First(); store.ForgetConnection(recent.Id);
        Assert.Single(store.RecentConnections()); Assert.Equal("sql-b",store.GetConnection()!.Server);
    }
}
