using Microsoft.Extensions.Configuration;
using SqlJobRunner;
using Xunit;

public class ExecuteJobTests
{
    private static Store NewStore() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["DataDirectory"]=Path.Combine(Path.GetTempPath(),"SqlJobRunnerTests",Guid.NewGuid().ToString("N")) }).Build());
    private static JobDefinition Job() => new() {JobName="test",Sql1="SELECT 1;"};
    [Fact]
    public void Definition_and_run_are_created_together()
    {
        var store=NewStore();store.SetConnection(new("fake"));var job=Job();
        var run=store.Execute(job,Guid.NewGuid().ToString("N"));
        Assert.Single(store.Jobs());Assert.Single(store.Runs());Assert.Equal(job.Id,run.JobId);Assert.Equal("Queued",run.Status);
        Assert.Equal(job.Sql1,run.Definition.Sql1);
    }
    [Fact]
    public void Missing_connection_leaves_no_saved_definition_or_run()
    {
        var store=NewStore();Assert.Throws<InvalidOperationException>(()=>store.Execute(Job(),Guid.NewGuid().ToString("N")));
        Assert.Empty(store.Jobs());Assert.Empty(store.Runs());
    }
    [Fact]
    public void Repeated_request_is_idempotent_even_after_completion()
    {
        var store=NewStore();store.SetConnection(new("fake"));var job=Job();var request=Guid.NewGuid().ToString("N");
        var run=store.Execute(job,request);run.Status="Succeeded";store.Complete(run);
        var repeat=store.Execute(job with {Id=Guid.NewGuid().ToString("N")},request);
        Assert.Equal(run.Id,repeat.Id);Assert.Single(store.Runs());Assert.Single(store.Jobs());
    }
    [Fact]
    public void Same_name_reuses_definition_and_blocks_concurrent_requests()
    {
        var store=NewStore();store.SetConnection(new("fake"));var job=Job();
        store.Execute(job,Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(()=>store.Execute(Job() with {Sql1="SELECT 2;"},Guid.NewGuid().ToString("N")));
        Assert.Single(store.Runs());Assert.Equal("SELECT 1;",Assert.Single(store.Jobs()).Sql1);
    }
    [Fact]
    public void New_explicit_request_can_run_existing_job_after_completion()
    {
        var store=NewStore();store.SetConnection(new("fake"));var job=Job();var run=store.Execute(job,Guid.NewGuid().ToString("N"));
        run.Status="Succeeded";store.Complete(run);
        var next=store.Execute(Job() with {Sql1="SELECT 2;"},Guid.NewGuid().ToString("N"));
        Assert.Single(store.Jobs());Assert.Equal(2,store.Runs().Count);Assert.Equal(job.Id,next.JobId);
        Assert.Equal("SELECT 1;",store.Runs().Single(r=>r.Id==run.Id).Definition.Sql1);
    }
    [Fact]
    public void Reusing_request_id_with_different_sql_is_rejected()
    {
        var store=NewStore();store.SetConnection(new("fake"));var job=Job();var request=Guid.NewGuid().ToString("N");store.Execute(job,request);
        Assert.Throws<InvalidOperationException>(()=>store.Execute(job with {Sql1="SELECT 2;"},request));Assert.Single(store.Runs());
    }
}
