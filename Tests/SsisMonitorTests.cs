using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlJobRunner;
using Xunit;

public class SsisMonitorTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T10:30:00+03:00");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Reader : ISsisErrorReader
    {
        public int Calls, ProbeCalls;
        public long Latest;
        public Task<long> LatestErrorId(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct)
        { ProbeCalls++; return Task.FromResult(Latest); }
        public DateTimeOffset From, Before;
        public string? Server;
        public Func<Task<SsisLookupResult>> Query = () => Task.FromResult(new SsisLookupResult([], []));
        public Task<SsisLookupResult> ReadRange(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct)
        { Calls++; Server=settings.Server; From=from; Before=before; return Query(); }
    }
    private static Store NewStore() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["DataDirectory"]=Path.Combine(Path.GetTempPath(),"SqlJobRunnerTests",Guid.NewGuid().ToString("N")) }).Build());

    [Fact]
    public async Task One_query_for_exact_48_hours_is_reused_and_refreshed_without_any_browser()
    {
        var store=NewStore(); var reader=new Reader(); var clock=new Clock();
        using var monitor=new SsisErrorMonitor(store,reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        await monitor.Refresh(false,default);
        Assert.Equal(clock.Now,reader.Before); Assert.Equal(TimeSpan.FromHours(48),reader.Before-reader.From);
        Assert.Equal(clock.Now,monitor.Current.UpdatedUtc);
        await monitor.Refresh(false,default); Assert.Equal(1,reader.Calls);
        clock.Now=clock.Now.AddSeconds(3);
        await monitor.Refresh(false,default); Assert.Equal(1,reader.Calls); Assert.Equal(1,reader.ProbeCalls);
        Assert.Equal(clock.Now,monitor.Current.Before);
        Assert.Equal(clock.Now,monitor.Current.CheckedUtc);
        Assert.NotEqual(clock.Now,monitor.Current.UpdatedUtc);
        clock.Now=clock.Now.AddMinutes(5);
        await monitor.Refresh(false,default); Assert.Equal(2,reader.Calls);
    }

    [Fact]
    public async Task Failed_refresh_preserves_last_successful_data_and_timestamp()
    {
        var reader=new Reader { Query=()=>Task.FromResult(new SsisLookupResult([
            new("1","EDW","EDW","MAIN.dtsx",null,null,[new("10",DateTimeOffset.UtcNow,"MAIN.dtsx","original error",null,null,null)])],[])) };
        var clock=new Clock(); using var monitor=new SsisErrorMonitor(NewStore(),reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        await monitor.Refresh(false,default); var success=monitor.Current;
        clock.Now=clock.Now.AddMinutes(1); reader.Query=()=>throw new InvalidOperationException("network unavailable");
        await monitor.Refresh(true,default);
        Assert.Equal(success.UpdatedUtc,monitor.Current.UpdatedUtc); Assert.Equal(1,monitor.Current.ErrorCount);
        Assert.Contains("network unavailable",monitor.Current.Error); Assert.False(monitor.Current.IsRefreshing);
    }

    [Fact]
    public async Task New_identifier_triggers_full_read_at_next_three_second_check()
    {
        var reader=new Reader(); var clock=new Clock();
        using var monitor=new SsisErrorMonitor(NewStore(),reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        await monitor.Refresh(false,default);
        reader.Latest=12;
        reader.Query=()=>Task.FromResult(new SsisLookupResult([
            new("1","EDW","EDW","MAIN.dtsx",null,null,[new("12",clock.Now,"MAIN.dtsx","new error",null,null,null)])],[]));
        clock.Now=clock.Now.AddSeconds(3); await monitor.Refresh(false,default);
        Assert.Equal(2,reader.Calls); Assert.Equal(1,reader.ProbeCalls); Assert.Equal(1,monitor.Current.ErrorCount);
        var updated=monitor.Current.UpdatedUtc;
        clock.Now=clock.Now.AddSeconds(3); await monitor.Refresh(false,default);
        Assert.Equal(2,reader.Calls); Assert.Equal(2,reader.ProbeCalls); Assert.Equal(updated,monitor.Current.UpdatedUtc);
    }

    [Fact]
    public async Task Expired_messages_are_pruned_locally_when_latest_identifier_is_unchanged()
    {
        var clock=new Clock(); var reader=new Reader { Latest=12 };
        var expires=clock.Now.AddHours(-48).AddSeconds(1);
        reader.Query=()=>Task.FromResult(new SsisLookupResult([
            new("1","EDW","EDW","MAIN.dtsx",null,null,[
                new("11",expires,"MAIN.dtsx","expired",null,null,null),
                new("12",clock.Now,"MAIN.dtsx","current",null,null,null)])],[]));
        using var monitor=new SsisErrorMonitor(NewStore(),reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        await monitor.Refresh(false,default); Assert.Equal(2,monitor.Current.ErrorCount);
        clock.Now=clock.Now.AddSeconds(3); await monitor.Refresh(false,default);
        Assert.Equal(1,reader.Calls); Assert.Equal(1,reader.ProbeCalls); Assert.Equal(1,monitor.Current.ErrorCount);
        Assert.Equal("12",monitor.Current.Executions![0].Errors[0].MessageId);
        Assert.Equal(clock.Now,monitor.Current.TableUpdatedUtc);
    }

    [Fact]
    public async Task Table_time_changes_only_when_data_changes_not_when_same_data_is_reloaded()
    {
        var clock=new Clock(); var reader=new Reader(); var messageTime=clock.Now;
        var message="first";
        reader.Query=()=>Task.FromResult(new SsisLookupResult([
            new("1","EDW","EDW","MAIN.dtsx",null,null,[new("12",messageTime,"MAIN.dtsx",message,null,null,null)])],[]));
        using var monitor=new SsisErrorMonitor(NewStore(),reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        await monitor.Refresh(true,default);
        var original=monitor.Current.TableUpdatedUtc;
        clock.Now=clock.Now.AddMinutes(1); await monitor.Refresh(true,default);
        Assert.Equal(original,monitor.Current.TableUpdatedUtc);
        Assert.Equal(clock.Now,monitor.Current.UpdatedUtc);
        clock.Now=clock.Now.AddMinutes(1); message="changed";
        await monitor.Refresh(true,default);
        Assert.Equal(clock.Now,monitor.Current.TableUpdatedUtc);
        clock.Now=clock.Now.AddMinutes(1); reader.Query=()=>Task.FromResult(new SsisLookupResult([],[]));
        await monitor.Refresh(true,default);
        Assert.Equal(clock.Now,monitor.Current.TableUpdatedUtc);
        Assert.Equal(0,monitor.Current.ErrorCount);
    }

    [Fact]
    public async Task Concurrent_refresh_is_not_duplicated_and_old_environment_results_are_discarded()
    {
        var store=NewStore(); var reader=new Reader(); var clock=new Clock();
        var pending=new TaskCompletionSource<SsisLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Query=()=>pending.Task;
        using var monitor=new SsisErrorMonitor(store,reader,clock,NullLogger<SsisErrorMonitor>.Instance);
        var first=monitor.Refresh(false,default); Assert.True(monitor.Current.IsRefreshing);
        await monitor.Refresh(true,default); Assert.Equal(1,reader.Calls);
        store.SetRequestSettings(new("OTHER")); Assert.Null(monitor.Current.UpdatedUtc);
        pending.SetResult(new([],[])); await first;
        Assert.Equal("OTHER",monitor.Current.Server); Assert.Null(monitor.Current.UpdatedUtc);
        reader.Query=()=>Task.FromResult(new SsisLookupResult([],[]));
        await monitor.Refresh(false,default);
        Assert.Equal(2,reader.Calls); Assert.Equal("OTHER",reader.Server); Assert.NotNull(monitor.Current.UpdatedUtc);
    }
}
