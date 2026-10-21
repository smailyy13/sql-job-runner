using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlJobRunner;

public interface IAgentClient
{
    Task Prepare(RunRecord run, ConnectionInput input, CancellationToken ct);
    Task Submit(RunRecord run, ConnectionInput input, CancellationToken ct);
    Task<bool> Resolve(RunRecord run, ConnectionInput input, CancellationToken ct);
    Task Start(RunRecord run, ConnectionInput input, CancellationToken ct);
    Task Poll(RunRecord run, ConnectionInput input, CancellationToken ct);
}
public sealed class AgentClient(Store store) : IAgentClient
{
    internal SqlConnection Connect(ConnectionInput input)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = input.Server, InitialCatalog = ConnectionDefaults.Database, IntegratedSecurity = input.IntegratedSecurity,
            Encrypt = input.Encrypt, TrustServerCertificate = input.TrustServerCertificate,
            ConnectTimeout = 8, ApplicationName = "SqlJobRunner"
        };
        if (!input.IntegratedSecurity) { b.UserID = input.UserName; b.Password = store.PasswordFor(input); }
        return new SqlConnection(b.ConnectionString);
    }
    public async Task<object> Test(ConnectionInput input, CancellationToken ct)
    {
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT @@SERVERNAME, COUNT(*) FROM msdb.dbo.sysjobs; SELECT TOP(1) session_id FROM msdb.dbo.syssessions ORDER BY session_id DESC; SELECT TOP(0) job_id FROM msdb.dbo.sysjobactivity; SELECT TOP(0) instance_id FROM msdb.dbo.sysjobhistory; SELECT TOP(0) step_id FROM msdb.dbo.sysjobsteps; SELECT TOP(0) JobName,JobCreationStatus,ErrorLog FROM EDWUTIL.OPR.EdwJobGeneratorLog;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct); var server = reader.IsDBNull(0) ? input.Server : reader.GetString(0); var count = reader.GetInt32(1);
        var hasSession = await reader.NextResultAsync(ct) && await reader.ReadAsync(ct);
        while (await reader.NextResultAsync(ct)) { }
        return new { server, visibleJobs = count, hasAgentSession = hasSession,
            message = "msdb ve EdwJobGeneratorLog okuma kontrolü tamamlandı. Prosedür EXECUTE ve Agent job başlatma yetkisi gerçek çalıştırmada doğrulanır." };
    }
    public async Task Prepare(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        GeneratorContract.Validate(run.Definition);
        run.GeneratorJobName = GeneratorContract.Token(run);
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT GETDATE(); SELECT TOP(0) JobName, JobCreationStatus, ErrorLog FROM EDWUTIL.OPR.EdwJobGeneratorLog;";
        await using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        run.ServerSubmittedAt = r.GetDateTime(0);
        while (await r.NextResultAsync(ct)) { }
    }
    public async Task Submit(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "EDWUTIL.OPR.EdwJobGenerator"; cmd.CommandType = CommandType.StoredProcedure; cmd.CommandTimeout = 60;
        GeneratorContract.AddParameters(cmd, run);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> Resolve(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        var prefix = GeneratorContract.Prefix(run);
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        // Prefix includes a durable, unique ASCII token. The exact job_id is pinned after discovery.
        cmd.CommandText = "SELECT TOP(2) job_id,name FROM msdb.dbo.sysjobs WHERE LEFT(name,LEN(@prefix))=@prefix;";
        cmd.Parameters.AddWithValue("@prefix",prefix);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0).ToString(); var name = r.GetString(1);
            if (await r.ReadAsync(ct)) throw new InvalidOperationException("Çalışma etiketi için birden fazla job bulundu; hiçbiri başlatılmadı.");
            run.AgentJobId = id; run.AgentJobName = name; run.BaselineHistoryId = 0; return true;
        }
        await r.DisposeAsync();
        await using var log = db.CreateCommand();
        log.CommandText = "SELECT TOP(1) JobCreationStatus,ErrorLog FROM EDWUTIL.OPR.EdwJobGeneratorLog WHERE LEFT(JobName,LEN(@prefix))=@prefix ORDER BY JobCreationTime DESC;";
        log.Parameters.AddWithValue("@prefix", prefix);
        await using var lr = await log.ExecuteReaderAsync(ct);
        if (await lr.ReadAsync(ct) && string.Equals(lr["JobCreationStatus"]?.ToString(), "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            run.Status = "Failed"; run.Message = "Prosedür job oluşturamadı: " + lr["ErrorLog"];
        }
        else
        {
            run.Status = "Unknown"; run.Message = "Benzersiz etiketli job henüz bulunamadı. Oluşturma/log görünürlüğü izleniyor; prosedür tekrar çağrılmayacak.";
        }
        return false;
    }
    public async Task Start(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var probe = db.CreateCommand();
        probe.CommandText = """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE job_id=@id AND start_execution_date IS NOT NULL)
              OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobhistory WHERE job_id=@id) THEN 1 ELSE 0 END
            """;
        probe.Parameters.AddWithValue("@id",Guid.Parse(run.AgentJobId!));
        // Even if someone externally started this uniquely-created job, do not start it again.
        if (Convert.ToInt32(await probe.ExecuteScalarAsync(ct)) == 1) return;
        await using var cmd = db.CreateCommand(); cmd.CommandText = "msdb.dbo.sp_start_job"; cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@job_id", Guid.Parse(run.AgentJobId!));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task Poll(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        var submitted = run.ServerSubmittedAt ?? throw new InvalidOperationException("Gönderim zamanı bulunamadı.");
        var submittedSecond = submitted.AddTicks(-(submitted.Ticks % TimeSpan.TicksPerSecond));
        await using var db = Connect(input); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT j.job_id, a.start_execution_date, a.stop_execution_date,
                a.last_executed_step_id, a.session_id, (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            FROM msdb.dbo.sysjobs j OUTER APPLY
                (SELECT TOP(1) * FROM msdb.dbo.sysjobactivity a WHERE a.job_id=j.job_id ORDER BY a.session_id DESC) a
            WHERE (@id IS NOT NULL AND j.job_id=@id) OR (@id IS NULL AND j.name=@name);
            """;
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = run.AgentJobId is null ? DBNull.Value : Guid.Parse(run.AgentJobId);
        cmd.Parameters.AddWithValue("@name", run.AgentJobName ?? run.Definition.AgentJobName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            if (DateTimeOffset.UtcNow - run.CreatedUtc > TimeSpan.FromMinutes(2)) run.Status = "Unknown";
            run.Message = "Agent job bulunamadı. Agent job adı ve msdb görünürlük yetkisini kontrol edin; tekrar başlatılmadı."; return;
        }
        var id = r.GetGuid(0);
        run.AgentJobId = id.ToString();
        var started = r.IsDBNull(1) ? (DateTime?)null : r.GetDateTime(1);
        var stopped = !r.IsDBNull(2);
        var currentSession = !r.IsDBNull(4) && !r.IsDBNull(5) && r.GetInt32(4) == r.GetInt32(5);
        var matches = started.HasValue && started.Value >= submittedSecond;
        await r.DisposeAsync();
        // sp_help_job exposes Agent's current step (last_executed_step_id + 1 is incorrect for branching jobs).
        if (matches && !stopped && currentSession)
        {
            await using var help = db.CreateCommand(); help.CommandText = "msdb.dbo.sp_help_job"; help.CommandType = CommandType.StoredProcedure;
            help.Parameters.AddWithValue("@job_id", id);
            await using var hr = await help.ExecuteReaderAsync(ct);
            if (await hr.ReadAsync(ct))
            {
                run.CurrentStep = hr["current_execution_step"] as string ?? "Agent adım bilgisi bekleniyor";
                var state = Convert.ToInt32(hr["current_execution_status"]);
                run.Message = state switch { 2 => "Agent iş parçacığı bekleniyor.", 3 => "Agent yeniden deneme aralığında.", 7 => "Sonlandırma işlemleri sürüyor.", _ => "SQL Server Agent çalışması izleniyor." };
            }
            run.Status = "Running";
        }
        await using var history = db.CreateCommand();
        history.CommandText = """
            SELECT instance_id, step_id, step_name, run_status, message, run_duration,
                msdb.dbo.agent_datetime(run_date,run_time) AS started
            FROM msdb.dbo.sysjobhistory
            WHERE job_id=@id AND instance_id>@baseline AND run_date>0
              AND msdb.dbo.agent_datetime(run_date,run_time)>=@submitted
            ORDER BY instance_id
            """;
        history.Parameters.AddWithValue("@id", id); history.Parameters.AddWithValue("@baseline", run.BaselineHistoryId);
        // Agent history is second-precision. Baseline excludes pre-existing rows for existing jobs.
        history.Parameters.AddWithValue("@submitted", submittedSecond);
        await using var h = await history.ExecuteReaderAsync(ct);
        var steps = new List<AgentStep>(); string? outcome = null; string? outcomeMessage = null;
        while (await h.ReadAsync(ct))
        {
            var step = new AgentStep(h.GetInt32(1), h.GetString(2), h.GetInt32(3), h.GetString(4), h.GetInt32(5));
            // Stop at the first overall outcome after submission: later externally started runs are unrelated.
            if (step.StepId == 0 && step.Status is 0 or 1 or 3)
            {
                outcome = step.Status == 1 ? "Succeeded" : step.Status == 3 ? "Canceled" : "Failed";
                outcomeMessage = step.Message; break;
            }
            steps.Add(step);
        }
        run.StepsJson = System.Text.Json.JsonSerializer.Serialize(steps, Store.Json);
        if (outcome is not null)
        {
            run.Status = outcome; run.Message = outcomeMessage!; run.CurrentStep = ""; return;
        }
        if (matches && !stopped && currentSession) return;
        if ((matches && (!currentSession || stopped)) || DateTimeOffset.UtcNow - run.CreatedUtc > TimeSpan.FromMinutes(2))
        {
            run.Status = "Unknown";
            run.Message = "Bu çalışmanın bitiş kaydı henüz doğrulanamadı. Agent geçmişi/servisi ve job adı kontrol edilmeli. İzleme sürüyor; otomatik tekrar çalıştırılmayacak.";
        }
    }
}
