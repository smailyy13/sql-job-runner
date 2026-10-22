using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlJobRunner;

// A row of EDWUTIL.OPR.EdwJobGeneratorRequests. The table only records the request and who took it;
// Status and the fields after it are derived from msdb (or the local run) and are not stored.
public record JobRequest
{
    public int RequestId { get; init; }
    public string JobName { get; init; } = "";
    public string? MIGPackageName { get; init; }
    public string? StgPackageName { get; init; }
    public bool StgFullInitialLoad { get; init; }
    public string? SkeyPackageName { get; init; }
    public string? LryPackageName { get; init; }
    public string? Sql1 { get; init; }
    public string? EdwPackageName { get; init; }
    public string? Sql2 { get; init; }
    public string? EdwdmPackageName { get; init; }
    public string RequestedBy { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public string? ExecutedBy { get; init; }
    public string? RunToken { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }
    public string Status { get; init; } = "Pending";
    public string? StatusMessage { get; init; }
    public string? CurrentStep { get; init; }
    public string? AgentJobName { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public JobDefinition ToDefinition() => new()
    {
        JobName = JobName, MIGPackageName = MIGPackageName, StgPackageName = StgPackageName, StgFullInitialLoad = StgFullInitialLoad,
        SkeyPackageName = SkeyPackageName, LryPackageName = LryPackageName, Sql1 = Sql1, EdwPackageName = EdwPackageName,
        Sql2 = Sql2, EdwdmPackageName = EdwdmPackageName
    };
}
public record AgentStatus(string Status, string? Message, string? CurrentStep, string? AgentJobName, DateTimeOffset? FinishedAt, DateTimeOffset? StartedAt = null);
public record RunRequestInput(string RequestId);

public interface IRequestStore
{
    Task<List<JobRequest>> List(CancellationToken ct);
    Task<int> Create(JobDefinition job, string requestedBy, CancellationToken ct);
    // Atomically takes a request with the employee's own connection: a waiting one (previousToken null) or one whose
    // previous attempt (previousToken) failed or was canceled.
    Task<ClaimResult> Claim(int id, string runToken, string? previousToken, ConnectionInput employee, CancellationToken ct);
    Task SaveFinishedAt(int id, string runToken, DateTimeOffset finishedAt, ConnectionInput employee, CancellationToken ct);
    Task Release(int id, string runToken, ConnectionInput employee, CancellationToken ct);
    // Agent state of taken requests, read from msdb by their run token. Missing keys: not readable yet.
    Task<Dictionary<int, AgentStatus>> Statuses(IReadOnlyList<JobRequest> taken, CancellationToken ct);
}

public enum ClaimResult { Claimed = 1, Taken = 0, Busy = 2 }

public static class RequestStates
{
    public static string FromRun(string status) => status is "Submitting" or "Starting" ? "Starting" : status;
    public static bool Final(string status) => status is "Succeeded" or "Failed" or "Canceled";
    // A job is running somewhere; a second request for the same job must wait.
    public static bool Busy(string status) => status is "Queued" or "Starting" or "Running";
    // A finished attempt that did not succeed can be run again with the same request.
    public static bool Rerunnable(string status) => status is "Failed" or "Canceled";
    public static bool SameJob(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    public static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";
}

public sealed class SqlRequestStore(Store store, AgentClient agent) : IRequestStore
{
    private const string Table = "EDWUTIL.OPR.EdwJobGeneratorRequests";
    private const string Columns = "RequestId,JobName,MIGPackageName,StgPackageName,StgFullInitialLoad,SkeyPackageName,LryPackageName,Sql1,EdwPackageName,Sql2,EdwdmPackageName,RequestedBy,RequestedAt,ExecutedBy,RunToken,ExecutedAt,FinishedAt";
    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        var settings = store.GetRequestSettings();
        var b = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server, InitialCatalog = ConnectionDefaults.Database, UserID = RequestCredentials.UserName, Password = RequestCredentials.Password,
            Encrypt = settings.Encrypt, TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = 8, ApplicationName = "SqlJobRunner Requests"
        };
        var db = new SqlConnection(b.ConnectionString);
        try { await db.OpenAsync(ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    private static string? Text(SqlDataReader r, string name) => r[name] is DBNull ? null : Convert.ToString(r[name]);
    private static DateTimeOffset? Time(SqlDataReader r, string name) => r[name] is DBNull ? null : (DateTimeOffset)r[name];
    public async Task<List<JobRequest>> List(CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {Table} ORDER BY RequestId DESC;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JobRequest>();
        while (await r.ReadAsync(ct))
            list.Add(new JobRequest
            {
                RequestId = (int)r["RequestId"], JobName = Text(r, "JobName") ?? "", MIGPackageName = Text(r, "MIGPackageName"),
                StgPackageName = Text(r, "StgPackageName"), StgFullInitialLoad = string.Equals(Text(r, "StgFullInitialLoad"), "YES", StringComparison.OrdinalIgnoreCase),
                SkeyPackageName = Text(r, "SkeyPackageName"), LryPackageName = Text(r, "LryPackageName"), Sql1 = Text(r, "Sql1"),
                EdwPackageName = Text(r, "EdwPackageName"), Sql2 = Text(r, "Sql2"), EdwdmPackageName = Text(r, "EdwdmPackageName"),
                RequestedBy = Text(r, "RequestedBy") ?? "", RequestedAt = Time(r, "RequestedAt")!.Value,
                ExecutedBy = Text(r, "ExecutedBy"), RunToken = Text(r, "RunToken"), ExecutedAt = Time(r, "ExecutedAt"), FinishedAt = Time(r, "FinishedAt")
            });
        return list;
    }
    public async Task<int> Create(JobDefinition j, string requestedBy, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Table}(JobName,MIGPackageName,StgPackageName,StgFullInitialLoad,SkeyPackageName,LryPackageName,Sql1,EdwPackageName,Sql2,EdwdmPackageName,RequestedBy)
            OUTPUT inserted.RequestId
            VALUES(@JobName,@MIGPackageName,@StgPackageName,@StgFullInitialLoad,@SkeyPackageName,@LryPackageName,@Sql1,@EdwPackageName,@Sql2,@EdwdmPackageName,@RequestedBy);
            """;
        foreach (var (name, value, size) in new (string, string?, int)[] {
            ("JobName",j.JobName,128),("MIGPackageName",j.MIGPackageName,300),("StgPackageName",j.StgPackageName,300),("SkeyPackageName",j.SkeyPackageName,300),
            ("LryPackageName",j.LryPackageName,300),("Sql1",j.Sql1,4000),("EdwPackageName",j.EdwPackageName,300),("Sql2",j.Sql2,4000),("EdwdmPackageName",j.EdwdmPackageName,300) })
            cmd.Parameters.Add("@" + name, SqlDbType.VarChar, size).Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        cmd.Parameters.Add("@StgFullInitialLoad", SqlDbType.VarChar, 3).Value = j.StgFullInitialLoad == true ? "YES" : "NO";
        cmd.Parameters.Add("@RequestedBy", SqlDbType.NVarChar, 128).Value = requestedBy;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
    // ExecutedBy is the server-verified login of the employee, not a name sent by the client.
    // The row lock serializes claims of this request, independently of its display name.
    public async Task<ClaimResult> Claim(int id, string runToken, string? previousToken, ConnectionInput employee, CancellationToken ct)
    {
        await using var db = agent.Connect(employee); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SET XACT_ABORT ON;
            DECLARE @result INT = 0, @found BIT = 0, @current VARCHAR(50), @allowed BIT = 0;
            BEGIN TRAN;
            SELECT @found = 1, @current = RunToken FROM {Table} WITH (UPDLOCK) WHERE RequestId = @id;
            -- The request must still be in the state the employee saw: waiting, or the same failed attempt.
            IF @found = 1 AND ((@current IS NULL AND @previous IS NULL) OR @current = @previous)
            BEGIN
                IF @current IS NULL SET @allowed = 1;
                ELSE
                BEGIN
                    -- Re-run only after the previous attempt's Agent outcome is failed (0) or canceled (3),
                    -- or the generator logged that it could not create the job.
                    DECLARE @prefix VARCHAR(60) = 'BTVY_' + @current + '_';
                    IF ISNULL((SELECT TOP(1) h.run_status FROM msdb.dbo.sysjobs j JOIN msdb.dbo.sysjobhistory h ON h.job_id = j.job_id AND h.step_id = 0
                               WHERE LEFT(j.name, LEN(@prefix)) = @prefix ORDER BY h.instance_id DESC), -1) IN (0, 3)
                       OR (NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs j WHERE LEFT(j.name, LEN(@prefix)) = @prefix)
                           AND EXISTS (SELECT 1 FROM EDWUTIL.OPR.EdwJobGeneratorLog l WHERE l.JobCreationStatus = 'FAILED' AND LEFT(l.JobName, LEN(@prefix)) = @prefix))
                        SET @allowed = 1;
                END
            END
            IF @allowed = 1
            BEGIN
                    UPDATE {Table} SET ExecutedBy = SUSER_SNAME(), RunToken = @token, ExecutedAt = SYSDATETIMEOFFSET(), FinishedAt = NULL
                    WHERE RequestId = @id AND ISNULL(RunToken, '') = ISNULL(@current, '');
                    SET @result = @@ROWCOUNT;
            END
            COMMIT;
            SELECT @result;
            """;
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@token", SqlDbType.VarChar, 50).Value = runToken;
        cmd.Parameters.Add("@previous", SqlDbType.VarChar, 50).Value = (object?)previousToken ?? DBNull.Value;
        return (ClaimResult)Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
    public async Task SaveFinishedAt(int id, string runToken, DateTimeOffset finishedAt, ConnectionInput employee, CancellationToken ct)
    {
        await using var db = agent.Connect(employee); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"UPDATE {Table} SET FinishedAt=@finished WHERE RequestId=@id AND RunToken=@token AND ExecutedBy = SUSER_SNAME() AND FinishedAt IS NULL;";
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@token", SqlDbType.VarChar, 50).Value = runToken;
        cmd.Parameters.Add("@finished", SqlDbType.DateTimeOffset).Value = finishedAt;
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task Release(int id, string runToken, ConnectionInput employee, CancellationToken ct)
    {
        await using var db = agent.Connect(employee); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"UPDATE {Table} SET ExecutedBy=NULL,RunToken=NULL,ExecutedAt=NULL,FinishedAt=NULL WHERE RequestId=@id AND RunToken=@token;";
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@token", SqlDbType.VarChar, 50).Value = runToken;
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<Dictionary<int, AgentStatus>> Statuses(IReadOnlyList<JobRequest> taken, CancellationToken ct)
    {
        var result = new Dictionary<int, AgentStatus>();
        if (taken.Count == 0) return result;
        await using var db = await Open(ct);
        // Two parameters per request; stay well under SQL Server's 2100-parameter limit.
        foreach (var chunk in taken.Chunk(500)) await ReadStatuses(db, chunk, result, ct);
        return result;
    }
    private static async Task ReadStatuses(SqlConnection db, JobRequest[] taken, Dictionary<int, AgentStatus> result, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        // The generator names the job BTVY_<token>_<timestamp>; the token is unique per run.
        var rows = string.Join(",", taken.Select((_, i) => $"(@id{i},@prefix{i})"));
        cmd.CommandText = $"""
            SELECT t.RequestId, j.name, a.start_execution_date,
                TODATETIMEOFFSET(a.start_execution_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) AS started,
                TODATETIMEOFFSET(a.stop_execution_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) AS stopped,
                CASE WHEN a.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions) THEN 1 ELSE 0 END AS current_session,
                h.run_status, h.message, s.step_id, s.step_name, l.JobCreationStatus, l.ErrorLog
            FROM (VALUES {rows}) t(RequestId, Prefix)
            OUTER APPLY (SELECT TOP(1) job_id, name FROM msdb.dbo.sysjobs WHERE LEFT(name, LEN(t.Prefix)) = t.Prefix) j
            OUTER APPLY (SELECT TOP(1) start_execution_date, stop_execution_date, session_id FROM msdb.dbo.sysjobactivity WHERE job_id = j.job_id ORDER BY session_id DESC) a
            OUTER APPLY (SELECT TOP(1) run_status, message FROM msdb.dbo.sysjobhistory WHERE job_id = j.job_id AND step_id = 0 ORDER BY instance_id DESC) h
            OUTER APPLY (SELECT TOP(1) step_id, step_name FROM msdb.dbo.sysjobhistory WHERE job_id = j.job_id AND step_id > 0 ORDER BY instance_id DESC) s
            OUTER APPLY (SELECT TOP(1) JobCreationStatus, ErrorLog FROM EDWUTIL.OPR.EdwJobGeneratorLog
                         WHERE j.name IS NULL AND LEFT(JobName, LEN(t.Prefix)) = t.Prefix ORDER BY JobCreationTime DESC) l;
            """;
        for (var i = 0; i < taken.Length; i++)
        {
            cmd.Parameters.Add($"@id{i}", SqlDbType.Int).Value = taken[i].RequestId;
            cmd.Parameters.Add($"@prefix{i}", SqlDbType.NVarChar, 128).Value = "BTVY_" + taken[i].RunToken + "_";
        }
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = (int)r["RequestId"];
            var job = Text(r, "name");
            var lastStep = r["step_id"] is DBNull ? null : $"{r["step_id"]}. {r["step_name"]}";
            if (job is null)
            {
                if (string.Equals(Text(r, "JobCreationStatus"), "FAILED", StringComparison.OrdinalIgnoreCase))
                    result[id] = new("Failed", "Prosedür job oluşturamadı: " + Text(r, "ErrorLog"), null, null, null);
                continue;
            }
            if (r["run_status"] is not DBNull)
            {
                var status = Convert.ToInt32(r["run_status"]) switch { 1 => "Succeeded", 3 => "Canceled", 0 => "Failed", _ => "Running" };
                result[id] = new(status, Text(r, "message"), null, job, Time(r, "stopped"), Time(r, "started"));
            }
            else if (r["start_execution_date"] is DBNull)
                result[id] = new("Starting", "Job oluşturuldu, Agent başlangıcı bekleniyor.", null, job, null);
            else if (r["stopped"] is DBNull && Convert.ToInt32(r["current_session"]) == 1)
                result[id] = new("Running", "SQL Server Agent çalışması izleniyor.", lastStep is null ? null : "Son tamamlanan adım: " + lastStep, job, null, Time(r, "started"));
            else
                result[id] = new("Unknown", "Agent çalışması durmuş görünüyor ancak sonuç kaydı bulunamadı.", null, job, Time(r, "stopped"), Time(r, "started"));
        }
    }
}

// Demo mode: same contract, kept in memory, no SQL connection. Status comes from the local demo run.
public sealed class DemoRequestStore : IRequestStore
{
    private readonly object gate = new();
    private readonly List<JobRequest> rows = [];
    public Task<List<JobRequest>> List(CancellationToken ct) { lock (gate) return Task.FromResult(rows.OrderByDescending(r => r.RequestId).ToList()); }
    public Task<int> Create(JobDefinition j, string requestedBy, CancellationToken ct)
    {
        lock (gate)
        {
            var id = rows.Count == 0 ? 1 : rows.Max(r => r.RequestId) + 1;
            rows.Add(new JobRequest
            {
                RequestId = id, JobName = j.JobName, MIGPackageName = j.MIGPackageName, StgPackageName = j.StgPackageName, StgFullInitialLoad = j.StgFullInitialLoad == true,
                SkeyPackageName = j.SkeyPackageName, LryPackageName = j.LryPackageName, Sql1 = j.Sql1, EdwPackageName = j.EdwPackageName, Sql2 = j.Sql2,
                EdwdmPackageName = j.EdwdmPackageName, RequestedBy = requestedBy, RequestedAt = DateTimeOffset.Now
            });
            return Task.FromResult(id);
        }
    }
    // Busy detection needs Agent state; in demo the caller's check against the monitored list covers it.
    public Task<ClaimResult> Claim(int id, string runToken, string? previousToken, ConnectionInput employee, CancellationToken ct)
    {
        lock (gate)
        {
            var i = rows.FindIndex(r => r.RequestId == id);
            if (i < 0 || rows[i].RunToken != previousToken) return Task.FromResult(ClaimResult.Taken);
            var login = employee.IntegratedSecurity ? RequestStates.CurrentUser : employee.UserName;
            rows[i] = rows[i] with { ExecutedBy = login, RunToken = runToken, ExecutedAt = DateTimeOffset.Now, FinishedAt = null };
            return Task.FromResult(ClaimResult.Claimed);
        }
    }
    public Task SaveFinishedAt(int id, string runToken, DateTimeOffset finishedAt, ConnectionInput employee, CancellationToken ct)
    {
        lock (gate)
        {
            var i = rows.FindIndex(r => r.RequestId == id && r.RunToken == runToken && r.FinishedAt is null);
            if (i >= 0) rows[i] = rows[i] with { FinishedAt = finishedAt };
        }
        return Task.CompletedTask;
    }
    public Task Release(int id, string runToken, ConnectionInput employee, CancellationToken ct)
    {
        lock (gate)
        {
            var i = rows.FindIndex(r => r.RequestId == id && r.RunToken == runToken);
            if (i >= 0) rows[i] = rows[i] with { ExecutedBy = null, RunToken = null, ExecutedAt = null };
        }
        return Task.CompletedTask;
    }
    public Task<Dictionary<int, AgentStatus>> Statuses(IReadOnlyList<JobRequest> taken, CancellationToken ct) => Task.FromResult(new Dictionary<int, AgentStatus>());
}

// Keeps a fresh copy of the request table with each request's current state.
public sealed class RequestMonitor(Store store, IRequestStore requests, ILogger<RequestMonitor> logger) : BackgroundService
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    // Finished attempts do not change; stop querying msdb for them. Keyed by run token: a re-run is a new attempt.
    private readonly Dictionary<string, AgentStatus> finished = new();
    public List<JobRequest> Requests { get; private set; } = [];
    public string? Error { get; private set; }
    public DateTimeOffset? UpdatedUtc { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Refresh(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    // Another server has other request ids; forget what was read from the previous one.
    public async Task Reset(CancellationToken ct)
    {
        await refreshGate.WaitAsync(ct);
        try { finished.Clear(); Requests = []; Error = null; UpdatedUtc = null; }
        finally { refreshGate.Release(); }
        await Refresh(ct);
    }
    public async Task Refresh(CancellationToken ct)
    {
        await refreshGate.WaitAsync(ct);
        try
        {
            var rows = await requests.List(ct);
            // Runs started on this computer, by the token they sent to the generator.
            var local = store.Runs().Where(r => r.RequestId is not null)
                .GroupBy(GeneratorContract.Token).ToDictionary(g => g.Key, g => g.First());
            var remote = rows.Where(r => r.RunToken is not null && finished.ContainsKey(r.RunToken)).ToDictionary(r => r.RequestId, r => finished[r.RunToken!]);
            string? statusError = null;
            var unknown = rows.Where(r => r.RunToken is not null && !local.ContainsKey(r.RunToken) && !finished.ContainsKey(r.RunToken)).ToList();
            try
            {
                foreach (var (id, status) in await requests.Statuses(unknown, ct))
                {
                    remote[id] = status;
                    if (RequestStates.Final(status.Status)) finished[unknown.First(r => r.RequestId == id).RunToken!] = status;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { statusError = "Job durumu msdb'den okunamadı: " + ex.Message; }
            Requests = rows.Select(r => Resolve(r, r.RunToken is null ? null : local.GetValueOrDefault(r.RunToken), remote.GetValueOrDefault(r.RequestId))).ToList();
            foreach (var row in Requests.Where(r => RequestStates.Final(r.Status) && r.RunToken is not null && r.FinishedAt is not null))
            {
                if (rows.First(r => r.RequestId == row.RequestId).FinishedAt is not null) continue;
                if (!local.TryGetValue(row.RunToken!, out var run) || run.ConnectionSnapshot is null) continue;
                try
                {
                    var employee = System.Text.Json.JsonSerializer.Deserialize<ConnectionInput>(run.ConnectionSnapshot, Store.Json)
                        ?? throw new InvalidOperationException("Çalıştıran kullanıcının bağlantısı bulunamadı.");
                    await requests.SaveFinishedAt(row.RequestId, row.RunToken!, row.FinishedAt!.Value, employee, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { statusError = "Bitiş zamanı kaydedilemedi: " + ex.Message; }
            }
            Error = statusError; UpdatedUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Error = "İstek tablosu okunamadı: " + ex.Message; logger.LogWarning("İstek tablosu okunamadı: {Message}", ex.Message); }
        finally { refreshGate.Release(); }
    }
    // The computer that runs a request has the richest view; everyone else reads msdb.
    internal static JobRequest Resolve(JobRequest row, RunRecord? run, AgentStatus? agent)
    {
        if (row.ExecutedBy is null) return row with { Status = "Pending" };
        if (run is not null)
            return row with { Status = RequestStates.FromRun(run.Status), StatusMessage = run.Message, CurrentStep = run.CurrentStep,
                AgentJobName = run.AgentJobName, StartedAt = run.AgentStartedAt, FinishedAt = run.AgentFinishedAt ?? row.FinishedAt };
        if (agent is not null)
            return row with { Status = agent.Status, StatusMessage = agent.Message, CurrentStep = agent.CurrentStep, AgentJobName = agent.AgentJobName, StartedAt = agent.StartedAt, FinishedAt = agent.FinishedAt ?? row.FinishedAt };
        if (DateTimeOffset.UtcNow - (row.ExecutedAt ?? row.RequestedAt) > TimeSpan.FromMinutes(2))
            return row with { Status = "Unknown", StatusMessage = "Çalışma etiketiyle oluşturulmuş Agent job'ı bulunamadı." };
        return row with { Status = "Starting", StatusMessage = "Job oluşturuluyor." };
    }
}
