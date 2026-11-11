using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlJobRunner;

public record RequestHistoryEntry(int InstanceId, int StepId, string Name, int Status, string Message,
    long DurationSeconds, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int SqlErrorNumber, int SqlSeverity, int Retries);

public static class HistoryTimes
{
    public static long DurationSeconds(int duration) => (duration / 10000L) * 3600 + (duration / 100 % 100) * 60 + duration % 100;
}

public sealed partial class SqlRequestStore
{
    // Read through the shared, SELECT-only msdb login so every workstation can see the same details.
    public async Task<List<RequestHistoryEntry>> History(JobRequest request, CancellationToken ct)
    {
        if (request.RunToken is null) return [];
        await using var db = await Open(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DECLARE @job UNIQUEIDENTIFIER=@importJob,@last INT=@importHistory,@previous INT=0;
            IF @job IS NULL
            BEGIN
                SELECT TOP(1) @job=job_id FROM msdb.dbo.sysjobs WHERE LEFT(name,LEN(@prefix))=@prefix ORDER BY date_created,job_id;
                SELECT @last=MIN(instance_id) FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=0 AND run_status IN (0,1,3);
            END
            ELSE
                SELECT @previous=ISNULL(MAX(instance_id),0) FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=0 AND instance_id<@last;
            SELECT instance_id, step_id, step_name, run_status, message, run_duration,
                CASE WHEN run_date > 0 THEN TODATETIMEOFFSET(msdb.dbo.agent_datetime(run_date,run_time),
                     DATEPART(TZOFFSET,SYSDATETIMEOFFSET())) END AS started,
                sql_message_id, sql_severity, retries_attempted
            FROM msdb.dbo.sysjobhistory
            WHERE job_id=@job AND instance_id>@previous AND (@last IS NULL OR instance_id<=@last)
              AND (@importStarted IS NULL OR (run_date>0 AND msdb.dbo.agent_datetime(run_date,run_time)>=@importStarted))
            ORDER BY instance_id;
            """;
        AddHistoryScope(cmd,request);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var entries = new List<RequestHistoryEntry>();
        while (await reader.ReadAsync(ct))
        {
            var start = Time(reader, "started");
            var seconds = HistoryTimes.DurationSeconds(Convert.ToInt32(reader["run_duration"]));
            var status = Convert.ToInt32(reader["run_status"]);
            entries.Add(new(Convert.ToInt32(reader["instance_id"]), Convert.ToInt32(reader["step_id"]),
                Text(reader,"step_name") ?? "", status, Text(reader,"message") ?? "", seconds,
                start, status == 4 ? null : start?.AddSeconds(seconds), Convert.ToInt32(reader["sql_message_id"]),
                Convert.ToInt32(reader["sql_severity"]), Convert.ToInt32(reader["retries_attempted"])));
        }
        return entries;
    }
}
