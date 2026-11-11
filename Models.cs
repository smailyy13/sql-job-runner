namespace SqlJobRunner;

public record ConnectionInput(string Server = "", bool IntegratedSecurity = true, string UserName = "",
    string? Password = null, bool Encrypt = false, bool TrustServerCertificate = true,
    string Database = "master", bool RememberPassword = true, string? SessionPasswordKey = null);
public static class ConnectionDefaults
{
    public const string Database = "master";
}
public record ConnectionView(string Server, bool IntegratedSecurity, string UserName, bool Encrypt,
    bool TrustServerCertificate, bool HasPassword, string Database, bool RememberPassword, string Id);
public record JobDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string JobName { get; init; } = "";
    public string AgentJobName { get; init; } = "";
    public string? MIGPackageName { get; init; }
    public string? StgPackageName { get; init; }
    public bool? StgFullInitialLoad { get; init; }
    public string? SkeyPackageName { get; init; }
    public string? LryPackageName { get; init; }
    public string? Sql1 { get; init; }
    public string? EdwPackageName { get; init; }
    public string? Sql2 { get; init; }
    public string? EdwdmPackageName { get; init; }
    public bool NotifySuccess { get; init; } = true;
    public bool NotifyFailure { get; init; } = true;
}
public record RunRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string JobId { get; init; } = "";
    public JobDefinition Definition { get; init; } = new();
    public string Status { get; set; } = "Queued";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public DateTimeOffset? AgentStartedAt { get; set; }
    public DateTimeOffset? AgentFinishedAt { get; set; }
    public DateTime? ServerSubmittedAt { get; set; }
    public string? AgentJobId { get; set; }
    public string? AgentJobName { get; set; }
    public string? GeneratorJobName { get; set; }
    public bool StartRequested { get; set; }
    public int BaselineHistoryId { get; set; }
    public string? ConnectionSnapshot { get; set; }
    public string CurrentStep { get; set; } = "";
    public string Message { get; set; } = "Kuyrukta bekliyor.";
    public string StepsJson { get; set; } = "[]";
    // Row in EDWUTIL.OPR.EdwJobGeneratorRequests this run executes.
    public int? RequestId { get; init; }
}
public record AgentStep(int StepId, string Name, int Status, string Message, int Duration);
public record Notification(string Id, string RunId, string JobName, string Status, DateTimeOffset CreatedUtc, bool Read);
public record ExecuteJobRequest(JobDefinition Job,string RequestId);
// Server holding EDWUTIL.OPR.EdwJobGeneratorRequests, reached with the embedded request login.
public record RequestSettings(string Server = RequestSettings.DefaultServer, bool Encrypt = false, bool TrustServerCertificate = true)
{
    public const string DefaultServer = "PASIFIK";
}
// The authenticated execution connection expires after three hours without a successful user operation.
public sealed class ConnectionSession
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(3);
    private readonly object gate = new();
    private sealed record Login(string Token, ConnectionInput Connection, RequestSettings Settings, DateTimeOffset LastActivityUtc);
    private Login? login;
    public string Authorize(ConnectionInput connection, RequestSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            var token = Guid.NewGuid().ToString("N");
            login = new(token, connection, settings, now);
            return token;
        }
    }
    private Login? Active(RequestSettings settings, DateTimeOffset now)
    {
        if (login is not null && (login.Settings != settings || now-login.LastActivityUtc >= IdleTimeout)) login = null;
        return login;
    }
    public void Clear() { lock (gate) login = null; }
    public bool IsActive(RequestSettings settings, DateTimeOffset now)
    { lock (gate) return Active(settings, now) is not null; }
    public ConnectionInput? GetConnection(string? token, RequestSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            var value = Active(settings, now);
            return value is not null && token == value.Token ? value.Connection : null;
        }
    }
    public bool Touch(string? token, RequestSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            var value = Active(settings, now);
            if (value is null || token != value.Token) return false;
            login = value with { LastActivityUtc = now };
            return true;
        }
    }
}
public static class RunStates
{
    public static bool Active(string status) => status is "Queued" or "Submitting" or "Starting" or "Running" or "Unknown";
}
