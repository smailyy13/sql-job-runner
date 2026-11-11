using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlJobRunner;

public record SsisErrorMessage(string MessageId, DateTimeOffset MessageTime, string PackageName,
    string Message, string? SourceName, string? SubcomponentName, string? ExecutionPath);
public record SsisExecutionErrors(string ExecutionId, string Folder, string Project, string Package,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, List<SsisErrorMessage> Errors, int? StepId = null, string? StepName = null, string? MatchMethod = null);

public static class SsisExecutionIds
{
    // Extract only explicit ID labels; SQL error numbers and timestamps are not execution IDs.
    public static List<long> Parse(IEnumerable<string> messages) => messages
        .SelectMany(message => Regex.Matches(message,
            @"\b(?:Execution\s*(?:ID|identifier)|Operation\s*ID|Yürütme\s*Kimliği|Çalıştırma\s*Kimliği)\s*[:=]\s*['""\[]?(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Cast<Match>())
        .Select(match => long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
        .Where(id => id > 0).Distinct().ToList();
}

public record SsisPackageTarget(string Server, string Folder, string Project, string Package, long? ReferenceId);
public record SsisFailedStep(int StepId, string Name, string Command, string Message, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);
public record SsisLookupResult(List<SsisExecutionErrors> Executions, List<string> Notices);
public record SsisCandidate(long Id, string Folder, string Project, string Package, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? ReferenceId);

public static class SsisCorrelation
{
    public static SsisPackageTarget? ParseTarget(string command)
    {
        var clean = command.Replace("\\\"", "\"");
        var path = Regex.Match(clean, @"\\SSISDB\\([^\\""\r\n]+)\\([^\\""\r\n]+)\\([^\\""\r\n]+\.dtsx)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var server = Regex.Match(clean, @"/SERVER\s+""*([^\s""]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var reference = Regex.Match(clean, @"/ENVREFERENCE\s+(\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!path.Success || !server.Success) return null;
        return new(server.Groups[1].Value, path.Groups[1].Value, path.Groups[2].Value, path.Groups[3].Value,
            reference.Success && long.TryParse(reference.Groups[1].Value, out var id) ? id : null);
    }
    public static bool AllowedFolder(string folder) => new[] { "EDW", "ERPEDW", "MessageUtilization" }.Contains(folder, StringComparer.OrdinalIgnoreCase);
    public static bool Matches(SsisCandidate candidate, SsisPackageTarget target, SsisFailedStep step, long? explicitId = null)
    {
        if (!AllowedFolder(candidate.Folder) || !string.Equals(candidate.Folder, target.Folder, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Project, target.Project, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Package, target.Package, StringComparison.OrdinalIgnoreCase)
            || (target.ReferenceId.HasValue && candidate.ReferenceId != target.ReferenceId)) return false;
        if (explicitId.HasValue) return candidate.Id == explicitId;
        // Agent history is second precision; its end is an exclusive upper bound plus one second.
        return candidate.CreatedAt >= step.StartedAt && candidate.CreatedAt < step.FinishedAt.AddSeconds(1)
            && candidate.StartedAt >= step.StartedAt && candidate.FinishedAt < step.FinishedAt.AddSeconds(1);
    }
    public static SsisCandidate? Unique(IEnumerable<SsisCandidate> candidates) => candidates.DistinctBy(c => c.Id).Take(2).ToArray() is [var only] ? only : null;
}

public interface ISsisErrorReader
{
    Task<long> LatestErrorId(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct);
    Task<SsisLookupResult> ReadRange(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct);
}

public sealed class SsisErrorReader(Store store) : ISsisErrorReader
{
    public string Server => store.GetRequestSettings().Server;
    private static async Task<SqlConnection> Open(RequestSettings settings, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server, InitialCatalog = "SSISDB", UserID = RequestCredentials.UserName,
            Password = RequestCredentials.Password, Encrypt = settings.Encrypt,
            TrustServerCertificate = settings.TrustServerCertificate, ConnectTimeout = 8,
            ApplicationName = "SqlJobRunner SSIS Errors"
        };
        var db = new SqlConnection(builder.ConnectionString);
        try { await db.OpenAsync(ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    // Return only the latest visible identifier; never transfer message bodies for change detection.
    public async Task<long> LatestErrorId(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await Open(settings, ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT TOP(1) m.event_message_id
            FROM catalog.event_messages AS m
            JOIN catalog.executions AS e ON e.execution_id=m.operation_id
            WHERE e.folder_name IN (N'EDW',N'ERPEDW',N'MessageUtilization')
              AND m.event_name=N'OnError'
              AND m.message_time>=@from AND m.message_time<@before
            ORDER BY m.event_message_id DESC;
            """;
        cmd.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = from;
        cmd.Parameters.Add("@before", SqlDbType.DateTimeOffset).Value = before;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
    public async Task<SsisLookupResult> ReadRange(RequestSettings settings, DateTimeOffset from, DateTimeOffset before, CancellationToken ct)
    {
        var results = new Dictionary<long, SsisExecutionErrors>();
        await using var db = await Open(settings, ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.execution_id,e.folder_name,e.project_name,e.package_name,e.start_time,e.end_time,
                   m.event_message_id,m.message_time,m.package_name,m.message,m.message_source_name,
                   m.subcomponent_name,m.execution_path
            FROM catalog.event_messages AS m
            JOIN catalog.executions AS e ON e.execution_id=m.operation_id
            WHERE e.folder_name IN (N'EDW',N'ERPEDW',N'MessageUtilization')
              AND m.event_name=N'OnError'
              AND m.message_time>=@from AND m.message_time<@before
            ORDER BY m.message_time DESC,m.event_message_id DESC;
            """;
        cmd.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = from;
        cmd.Parameters.Add("@before", SqlDbType.DateTimeOffset).Value = before;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt64(0);
            if (!results.TryGetValue(id, out var execution))
            {
                execution = new(id.ToString(CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2),
                    r.GetString(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), []);
                results.Add(id, execution);
            }
            execution.Errors.Add(new(r.GetInt64(6).ToString(CultureInfo.InvariantCulture),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? "" : r.GetString(8),
                r.IsDBNull(9) ? "" : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return new(results.Values.ToList(), []);
    }
}

public static class SsisDays
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
    public static DateOnly Of(DateTimeOffset timestamp) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, Zone).DateTime);
    public static (DateTimeOffset From, DateTimeOffset Before) Bounds(DateOnly day)
    {
        var from = day.ToDateTime(TimeOnly.MinValue);
        var before = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (new(from, Zone.GetUtcOffset(from)), new(before, Zone.GetUtcOffset(before)));
    }
}
