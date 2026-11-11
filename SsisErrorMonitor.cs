namespace SqlJobRunner;

public record SsisErrorSnapshot(string Server, DateTimeOffset? From = null, DateTimeOffset? Before = null,
    DateTimeOffset? UpdatedUtc = null, DateTimeOffset? LastAttemptUtc = null, bool IsRefreshing = false,
    string? Error = null, List<SsisExecutionErrors>? Executions = null, DateTimeOffset? CheckedUtc = null, DateTimeOffset? TableUpdatedUtc = null)
{
    public int ErrorCount => Executions?.Sum(e => e.Errors.Count) ?? 0;
}

// One refresh loop per application, independent of browser tabs and panel visibility.
public sealed class SsisErrorMonitor(Store store, ISsisErrorReader reader, TimeProvider clock,
    ILogger<SsisErrorMonitor> logger) : BackgroundService
{
    private sealed record CacheEntry(RequestSettings Settings, SsisErrorSnapshot Value, long LatestId = 0);
    private CacheEntry? entry;
    private readonly SemaphoreSlim gate = new(1, 1);
    public SsisErrorSnapshot Current
    {
        get
        {
            var settings = store.GetRequestSettings();
            var cached = Volatile.Read(ref entry);
            return cached?.Settings == settings ? cached.Value : new(settings.Server);
        }
    }
    public async Task Refresh(bool force, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            var settings = store.GetRequestSettings();
            var cached = Volatile.Read(ref entry);
            var previous = cached?.Settings == settings ? cached.Value : new SsisErrorSnapshot(settings.Server);
            var now = clock.GetUtcNow();
            if (!force && previous.LastAttemptUtc is {} last && now-last < TimeSpan.FromSeconds(previous.Error is null ? 3 : 10)) return;
            Volatile.Write(ref entry, new(settings, previous with { LastAttemptUtc = now, IsRefreshing = true }, cached?.Settings == settings ? cached.LatestId : 0));
            try
            {
                var from = now.AddHours(-48);
                var latestId = cached?.Settings == settings ? cached.LatestId : 0;
                var fullRead = force || previous.UpdatedUtc is null || now-previous.UpdatedUtc >= TimeSpan.FromMinutes(5);
                if (!fullRead)
                {
                    var latest = await reader.LatestErrorId(settings, from, now, ct);
                    fullRead = latest != latestId;
                }
                if (fullRead)
                {
                    var result = await reader.ReadRange(settings, from, now, ct);
                    latestId = result.Executions.SelectMany(e => e.Errors)
                        .Select(e => long.TryParse(e.MessageId, out var id) ? id : 0).DefaultIfEmpty(0).Max();
                    var finished = clock.GetUtcNow();
                    var tableUpdated = previous.UpdatedUtc is null || !SameRows(previous.Executions ?? [], result.Executions)
                        ? finished : previous.TableUpdatedUtc;
                    if (store.GetRequestSettings() == settings)
                        Volatile.Write(ref entry, new(settings, new(settings.Server, from, now, finished, now,
                            Executions: result.Executions, CheckedUtc: finished, TableUpdatedUtc: tableUpdated), latestId));
                }
                else if (store.GetRequestSettings() == settings)
                {
                    // Keep the rolling window accurate without querying message bodies again.
                    var executions = (previous.Executions ?? []).Select(e => e with
                        { Errors = e.Errors.Where(m => m.MessageTime >= from && m.MessageTime < now).ToList() })
                        .Where(e => e.Errors.Count > 0).ToList();
                    Volatile.Write(ref entry, new(settings, previous with { From = from, Before = now,
                        LastAttemptUtc = now, CheckedUtc = clock.GetUtcNow(), Error = null, IsRefreshing = false,
                        Executions = executions, TableUpdatedUtc = executions.Sum(e => e.Errors.Count) != previous.ErrorCount
                            ? clock.GetUtcNow() : previous.TableUpdatedUtc }, latestId));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SSIS errors could not be refreshed on {Server}", settings.Server);
                if (store.GetRequestSettings() == settings)
                    Volatile.Write(ref entry, new(settings, previous with { LastAttemptUtc = now,
                        Error = "SSIS hataları okunamadı: " + ex.Message }, cached?.Settings == settings ? cached.LatestId : 0));
            }
            finally
            {
                var value = Volatile.Read(ref entry);
                if (value?.Settings == settings && value.Value.IsRefreshing)
                    Volatile.Write(ref entry, value with { Value = value.Value with { IsRefreshing = false } });
            }
        }
        finally { gate.Release(); }
    }
    private static bool SameRows(List<SsisExecutionErrors> left, List<SsisExecutionErrors> right)
    {
        if (left.Count != right.Count) return false;
        return left.OrderBy(e => e.ExecutionId, StringComparer.Ordinal)
            .Zip(right.OrderBy(e => e.ExecutionId, StringComparer.Ordinal))
            .All(pair =>
                (pair.First.ExecutionId, pair.First.Folder, pair.First.Project, pair.First.Package, pair.First.StartedAt,
                    pair.First.FinishedAt, pair.First.StepId, pair.First.StepName, pair.First.MatchMethod)
                == (pair.Second.ExecutionId, pair.Second.Folder, pair.Second.Project, pair.Second.Package, pair.Second.StartedAt,
                    pair.Second.FinishedAt, pair.Second.StepId, pair.Second.StepName, pair.Second.MatchMethod)
                && pair.First.Errors.OrderBy(e => e.MessageId, StringComparer.Ordinal)
                    .SequenceEqual(pair.Second.Errors.OrderBy(e => e.MessageId, StringComparer.Ordinal)));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do { await Refresh(false, stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
