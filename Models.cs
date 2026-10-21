namespace SqlJobRunner;

public record ConnectionInput(string Server = "", bool IntegratedSecurity = true, string UserName = "",
    string? Password = null, bool Encrypt = false, bool TrustServerCertificate = true,
    string Database = "EDWTEMP", bool RememberPassword = true, string? SessionPasswordKey = null);
public static class ConnectionDefaults
{
    public const string Database = "EDWTEMP";
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
}
public record AgentStep(int StepId, string Name, int Status, string Message, int Duration);
public record Notification(string Id, string RunId, string JobName, string Status, DateTimeOffset CreatedUtc, bool Read);
public record ExecuteJobRequest(JobDefinition Job,string RequestId);
public static class RunStates
{
    public static bool Active(string status) => status is "Queued" or "Submitting" or "Starting" or "Running" or "Unknown";
}
