using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace SqlJobRunner;

public sealed class Store
{
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly Dictionary<string,string> sessionPasswords = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string DataDirectory { get; }
    public Store(IConfiguration config)
    {
        DataDirectory = config["DataDirectory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlJobRunner");
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "jobs.db"), DefaultTimeout = 10 }.ToString();
        using var db = Open();
        Execute(db, "PRAGMA journal_mode=WAL;");
        Execute(db, "CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(id TEXT PRIMARY KEY, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS notifications(id TEXT PRIMARY KEY, body TEXT NOT NULL);");
        Execute(db, "CREATE TABLE IF NOT EXISTS recent_connections(id TEXT PRIMARY KEY, body TEXT NOT NULL);");
        Execute(db, "CREATE TABLE IF NOT EXISTS app_settings(id TEXT PRIMARY KEY, body TEXT NOT NULL);");
        requestDefaults = new RequestSettings(
            string.IsNullOrWhiteSpace(config["Requests:Server"]) ? RequestSettings.DefaultServer : config["Requests:Server"]!,
            config.GetValue("Requests:Encrypt", false), config.GetValue("Requests:TrustServerCertificate", true));
        // Migrate the previous development default once; later explicit environment choices are retained.
        Execute(db, "CREATE TABLE IF NOT EXISTS local_migrations(name TEXT PRIMARY KEY);");
        using var migration = db.CreateCommand();
        migration.CommandText = "SELECT COUNT(*) FROM local_migrations WHERE name='production-default-v1'";
        if (Convert.ToInt32(migration.ExecuteScalar()) == 0)
        {
            var saved = All<RequestSettings>("app_settings").SingleOrDefault();
            using var tx = db.BeginTransaction();
            if (requestDefaults.Server.Equals("PASIFIK", StringComparison.OrdinalIgnoreCase)
                && saved?.Server.Equals(@"SRVDEV\PASIFIK", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var update = db.CreateCommand(); update.Transaction = tx;
                update.CommandText = "UPDATE app_settings SET body=$body WHERE id='requests'";
                update.Parameters.AddWithValue("$body", JsonSerializer.Serialize(saved with { Server = "PASIFIK" }, Json));
                update.ExecuteNonQuery();
            }
            using var mark = db.CreateCommand(); mark.Transaction = tx;
            mark.CommandText = "INSERT INTO local_migrations(name) VALUES('production-default-v1')";
            mark.ExecuteNonQuery(); tx.Commit();
        }
    }
    private readonly RequestSettings requestDefaults;
    // Saved from the Settings dialog; until then appsettings.json, then the built-in default.
    public RequestSettings GetRequestSettings() { lock (gate) return All<RequestSettings>("app_settings").SingleOrDefault() ?? requestDefaults; }
    public void SetRequestSettings(RequestSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Server)) throw new ArgumentException("İstek sunucusu zorunlu.");
        lock (gate) Save("app_settings", "requests", settings with { Server = settings.Server.Trim() });
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static void Execute(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, val) in args) cmd.Parameters.AddWithValue(key, val ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    private List<T> All<T>(string table)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT body FROM {table}";
        using var r = cmd.ExecuteReader(); var list = new List<T>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json)!);
        return list;
    }
    private void Save<T>(string table, string id, T value)
    {
        using var db = Open(); Execute(db, $"INSERT INTO {table}(id,body) VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body",
            ("$id", id), ("$body", JsonSerializer.Serialize(value, Json)));
    }
    public List<JobDefinition> Jobs() { lock (gate) return All<JobDefinition>("jobs"); }
    public List<RunRecord> Runs() { lock (gate) return All<RunRecord>("runs").OrderByDescending(r => r.CreatedUtc).ToList(); }
    public bool SaveRun(RunRecord run)
    {
        lock (gate)
        {
            if (Runs().Any(r => r.Id == run.Id && !RunStates.Active(r.Status))) return false;
            Save("runs", run.Id, run);
            return true;
        }
    }
    public bool HasActiveRun(string jobName) { lock (gate) return Runs().Any(r => RunStates.Active(r.Status) && r.Definition.JobName.Equals(jobName.Trim(), StringComparison.OrdinalIgnoreCase)); }
    public void CloseTracking(string id)
    {
        lock (gate)
        {
            var run = Runs().SingleOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("Çalışma bulunamadı.");
            if (run.Status != "Unknown") throw new InvalidOperationException("Yalnız durumu belirsiz çalışmanın izlemesi kapatılabilir.");
            run.Status = "Unverified"; run.CurrentStep = "";
            run.Message = "Kullanıcı izlemeyi kapattı. SQL Server Agent job'ı iptal edilmedi; gerçek sonucu doğrulanmadı.";
            Complete(run);
        }
    }
    public void SaveJob(JobDefinition job)
    {
        lock (gate)
        {
            if (Runs().Any(r => r.JobId == job.Id && RunStates.Active(r.Status))) throw new InvalidOperationException("Aktif çalışması olan job düzenlenemez.");
            if (Jobs().Any(j => j.Id != job.Id && j.JobName.Equals(job.JobName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Bu job adı zaten kayıtlı.");
            Save("jobs", job.Id, job);
        }
    }
    public RunRecord Execute(JobDefinition job,string requestId,int? jobRequestId=null,ConnectionInput? executionConnection=null)
    {
        lock(gate)
        {
            GeneratorContract.Validate(job);
            if(!Guid.TryParse(requestId,out var requestGuid)) throw new ArgumentException("Geçersiz çalışma isteği.");
            var id=requestGuid.ToString("N");
            job=job with {JobName=job.JobName.Trim(),AgentJobName=""};
            var prior=Runs().FirstOrDefault(r=>r.Id==id);
            if(prior is not null)
            {
                if(JsonSerializer.Serialize(job with {Id=prior.Definition.Id},Json)!=JsonSerializer.Serialize(prior.Definition,Json))
                    throw new InvalidOperationException("Bu çalışma isteği farklı bir EXEC için kullanılmış. Yeni job penceresi açın.");
                return prior;
            }
            var connection=executionConnection ?? (jobRequestId.HasValue ? GetExecutionConnection() : GetConnection()) ?? throw new InvalidOperationException("Önce SQL Server bağlantısını kurun.");
            var sameName=jobRequestId is null ? Jobs().FirstOrDefault(j=>j.JobName.Equals(job.JobName,StringComparison.OrdinalIgnoreCase)) : null;
            if(sameName is not null && sameName.Id!=job.Id)
            {
                if(Jobs().Any(j=>j.Id==job.Id)) throw new InvalidOperationException("Bu job adı zaten kayıtlı.");
                job=job with {Id=sameName.Id};
            }
            if(Runs().Any(r=>jobRequestId is null ? r.JobId==job.Id && RunStates.Active(r.Status) : r.RequestId==jobRequestId)) throw new InvalidOperationException("Bu job için aktif veya durumu belirsiz bir çalışma var.");
            var run=new RunRecord {Id=id,JobId=job.Id,Definition=job,ConnectionSnapshot=JsonSerializer.Serialize(connection,Json),RequestId=jobRequestId};
            using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO jobs(id,body) VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body";
            cmd.Parameters.AddWithValue("$id",job.Id);cmd.Parameters.AddWithValue("$body",JsonSerializer.Serialize(job,Json));cmd.ExecuteNonQuery();
            cmd.CommandText="INSERT INTO runs(id,body) VALUES($id,$body)";cmd.Parameters["$id"].Value=run.Id;cmd.Parameters["$body"].Value=JsonSerializer.Serialize(run,Json);cmd.ExecuteNonQuery();
            tx.Commit();return run;
        }
    }
    public void DeleteJob(string id)
    {
        lock (gate)
        {
            if (Runs().Any(r => r.JobId == id && RunStates.Active(r.Status))) throw new InvalidOperationException("Aktif job silinemez.");
            using var db = Open(); Execute(db, "DELETE FROM jobs WHERE id=$id", ("$id", id));
        }
    }
    public RunRecord Enqueue(string id)
    {
        lock (gate)
        {
            var job = Jobs().SingleOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("Job bulunamadı.");
            if (Runs().Any(r => r.JobId == id && r.RequestId is not null)) throw new InvalidOperationException("Bu istek daha önce çalıştırılmış. Her istek yalnızca bir kez çalıştırılabilir.");
            if (Runs().Any(r => RunStates.Active(r.Status) && r.JobId == job.Id))
                throw new InvalidOperationException("Bu job için aktif veya durumu belirsiz bir çalışma var.");
            var connection = GetConnection() ?? throw new InvalidOperationException("Önce bağlantıyı kaydedin.");
            var run = new RunRecord { JobId = id, Definition = job, ConnectionSnapshot = JsonSerializer.Serialize(connection, Json) };
            using var db = Open(); using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO runs(id,body) VALUES($id,$body)";
            cmd.Parameters.AddWithValue("$id", run.Id); cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(run, Json)); cmd.ExecuteNonQuery();
            tx.Commit(); return run;
        }
    }
    public void Complete(RunRecord run)
    {
        lock (gate)
        {
            if (Runs().Any(r => r.Id == run.Id && !RunStates.Active(r.Status))) return;
            run.FinishedUtc ??= DateTimeOffset.UtcNow;
            // Completion and its notification are committed together, including after crash recovery.
            using var db = Open(); using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE runs SET body=$body WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", run.Id); cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(run, Json)); cmd.ExecuteNonQuery();
            if ((run.Status == "Succeeded" && run.Definition.NotifySuccess) || (run.Status != "Succeeded" && run.Definition.NotifyFailure))
            {
                var n = new Notification(run.Id, run.Id, run.Definition.JobName, run.Status, DateTimeOffset.UtcNow, false);
                cmd.CommandText = "INSERT OR IGNORE INTO notifications(id,body) VALUES($id,$body)";
                cmd.Parameters["$body"].Value = JsonSerializer.Serialize(n, Json); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public List<Notification> Notifications() { lock (gate) return All<Notification>("notifications").OrderByDescending(n => n.CreatedUtc).ToList(); }
    public void ReadNotifications() { lock (gate) foreach (var n in Notifications().Where(n => !n.Read)) Save("notifications", n.Id, n with { Read = true }); }
    public ConnectionInput? GetConnection()
    {
        lock(gate) { var c=All<ConnectionInput>("settings").SingleOrDefault();return c is null?null:c with {Database=ConnectionDefaults.Database}; }
    }
    public ConnectionInput GetExecutionConnection()
    {
        lock (gate)
        {
            var settings = GetRequestSettings();
            var saved = GetConnection();
            var connection = saved is not null && saved.Server.Equals(settings.Server, StringComparison.OrdinalIgnoreCase)
                ? saved : new ConnectionInput(Server: settings.Server);
            return connection with { Server = settings.Server, Encrypt = settings.Encrypt,
                TrustServerCertificate = settings.TrustServerCertificate, Database = ConnectionDefaults.Database };
        }
    }
    // The database is fixed, so records saved under an older default share the same id.
    public static string ConnectionId(ConnectionInput c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{c.Server.ToUpperInvariant()}|{ConnectionDefaults.Database.ToUpperInvariant()}|{c.IntegratedSecurity}|{c.UserName.ToUpperInvariant()}")))[..24];
    public ConnectionView View(ConnectionInput c)
    {
        lock(gate) return new(c.Server,c.IntegratedSecurity,c.UserName,c.Encrypt,c.TrustServerCertificate,
            c.Password is not null || (c.SessionPasswordKey is not null && sessionPasswords.ContainsKey(c.SessionPasswordKey)),ConnectionDefaults.Database,c.RememberPassword,ConnectionId(c));
    }
    public List<ConnectionView> RecentConnections() { lock(gate) return All<ConnectionInput>("recent_connections").Select(View).DistinctBy(c => c.Id).ToList(); }
    public void ForgetConnection(string id)
    {
        lock(gate)
        {
            using var db = Open(); var stale = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id,body FROM recent_connections"; using var r = cmd.ExecuteReader();
                while (r.Read()) if (r.GetString(0) == id || ConnectionId(JsonSerializer.Deserialize<ConnectionInput>(r.GetString(1), Json)!) == id) stale.Add(r.GetString(0));
            }
            foreach (var key in stale) Execute(db,"DELETE FROM recent_connections WHERE id=$id",("$id",key));
        }
    }
    public ConnectionInput DraftConnection(ConnectionInput input)
    {
        lock(gate)
        {
            input=input with {Database=ConnectionDefaults.Database};
            var old = new[] {GetConnection()}.Concat(All<ConnectionInput>("recent_connections")).FirstOrDefault(c => c is not null &&
                c.Server.Equals(input.Server,StringComparison.OrdinalIgnoreCase) && c.UserName.Equals(input.UserName,StringComparison.OrdinalIgnoreCase) && c.IntegratedSecurity == input.IntegratedSecurity);
            return input with { Password = input.IntegratedSecurity ? null : input.Password is not null ? Protect(input.Password) : old?.Password,
                SessionPasswordKey = input.IntegratedSecurity || input.Password is not null ? null : old?.SessionPasswordKey };
        }
    }
    public string? PasswordFor(ConnectionInput input)
    {
        lock(gate)
        {
            if (input.IntegratedSecurity) return null;
            var current=GetConnection();
            if(current is not null && !current.IntegratedSecurity && current.Server.Equals(input.Server,StringComparison.OrdinalIgnoreCase) && current.UserName.Equals(input.UserName,StringComparison.OrdinalIgnoreCase))
            {
                if(current.Password is not null) return Unprotect(current.Password);
                if(current.SessionPasswordKey is not null && sessionPasswords.TryGetValue(current.SessionPasswordKey,out var fresh)) return Unprotect(fresh);
            }
            if (input.Password is not null) return Unprotect(input.Password);
            if (input.SessionPasswordKey is not null && sessionPasswords.TryGetValue(input.SessionPasswordKey,out var value)) return Unprotect(value);
            throw new InvalidOperationException("SQL parolası bulunamadı. Parolayı hatırla kapalıysa uygulama yeniden açıldıktan sonra parolayı tekrar girin.");
        }
    }
    public void SetConnection(ConnectionInput input)
    {
        lock (gate)
        {
            var saved = DraftConnection(input);
            var password = saved.Password;
            if (password is null && saved.SessionPasswordKey is not null) sessionPasswords.TryGetValue(saved.SessionPasswordKey,out password);
            if (!input.IntegratedSecurity && !input.RememberPassword && password is not null)
            {
                var key = Guid.NewGuid().ToString("N"); sessionPasswords[key] = password;
                saved = saved with { Password = null, SessionPasswordKey = key };
            }
            else saved = saved with { Password = password, SessionPasswordKey = null };
            Save("settings", "connection", saved);
            Save("recent_connections",ConnectionId(saved),saved);
        }
    }
    private static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Parola saklama Windows gerektirir.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
    public static string? Unprotect(string? value)
    {
        if (value is null) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Parola okuma Windows gerektirir.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    }
}
