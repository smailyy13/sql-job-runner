using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlJobRunner;

// Immutable reference to one completed historical Agent run. Fits existing VARCHAR(50) RunToken.
public record ImportedExecution(Guid JobId, int HistoryId, int Outcome)
{
    public string Status => Outcome switch { 1 => "Succeeded", 3 => "Canceled", _ => "Failed" };
    public static ImportedExecution? Parse(string? token)
    {
        var parts = token?.Split(':');
        return parts is {Length:4} && parts[0] == "H" && Guid.TryParseExact(parts[1],"N",out var job)
            && int.TryParse(parts[2],out var history) && history>0 && int.TryParse(parts[3],out var outcome) && outcome is 0 or 1 or 3
            ? new(job,history,outcome) : null;
    }
    public static AgentStatus Snapshot(JobRequest row)
    {
        var imported = Parse(row.RunToken) ?? throw new ArgumentException("Geçersiz geçmiş referansı.");
        return new(imported.Status,"Aktarılan geçmiş kayıt. Agent ayrıntıları silinmiş veya erişilemiyor olabilir.",null,
            row.JobName,row.FinishedAt,row.ExecutedAt);
    }
}

public sealed partial class SqlRequestStore
{
    private static void AddHistoryScope(SqlCommand cmd,JobRequest request)
    {
        var imported=ImportedExecution.Parse(request.RunToken);
        cmd.Parameters.Add("@importJob",SqlDbType.UniqueIdentifier).Value=(object?)imported?.JobId??DBNull.Value;
        cmd.Parameters.Add("@importHistory",SqlDbType.Int).Value=(object?)imported?.HistoryId??DBNull.Value;
        cmd.Parameters.Add("@prefix",SqlDbType.NVarChar,128).Value="BTVY_"+request.RunToken+"_";
        cmd.Parameters.Add("@importStarted",SqlDbType.DateTime2).Value=imported is not null ? (object?)request.ExecutedAt?.DateTime??DBNull.Value : DBNull.Value;
    }
    private static async Task ReadImportedStatuses(SqlConnection db,JobRequest[] rows,Dictionary<int,AgentStatus> result,CancellationToken ct)
    {
        if(rows.Length==0)return;
        await using var cmd=db.CreateCommand();
        var values=string.Join(",",rows.Select((_,i)=>$"(@request{i},@job{i},@history{i})"));
        cmd.CommandText=$"""
            SELECT t.RequestId,h.run_status,h.message,h.run_duration,
                CASE WHEN h.run_date>0 THEN TODATETIMEOFFSET(msdb.dbo.agent_datetime(h.run_date,h.run_time),
                    DATEPART(TZOFFSET,SYSDATETIMEOFFSET())) END AS started
            FROM (VALUES {values}) t(RequestId,JobId,HistoryId)
            LEFT JOIN msdb.dbo.sysjobhistory h ON h.job_id=t.JobId AND h.instance_id=t.HistoryId AND h.step_id=0;
            """;
        for(var i=0;i<rows.Length;i++)
        {
            var imported=ImportedExecution.Parse(rows[i].RunToken)!;
            cmd.Parameters.Add("@request"+i,SqlDbType.Int).Value=rows[i].RequestId;
            cmd.Parameters.Add("@job"+i,SqlDbType.UniqueIdentifier).Value=imported.JobId;
            cmd.Parameters.Add("@history"+i,SqlDbType.Int).Value=imported.HistoryId;
            result[rows[i].RequestId]=ImportedExecution.Snapshot(rows[i]);
        }
        var byId=rows.ToDictionary(r=>r.RequestId);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var id=(int)reader["RequestId"]; var row=byId[id];
            if(reader["run_status"] is DBNull)continue;
            var status=Convert.ToInt32(reader["run_status"]) switch {1=>"Succeeded",3=>"Canceled",0=>"Failed",_=>"Unknown"};
            var start=Time(reader,"started");
            result[id]=new(status,Text(reader,"message"),null,row.JobName,
                start?.AddSeconds(HistoryTimes.DurationSeconds(Convert.ToInt32(reader["run_duration"]))),start);
        }
    }
}
