using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace SqlJobRunner;

public static class GeneratorContract
{
    // @JobName is VARCHAR(50), including the BTVY_ prefix and _yyyyMMdd_HHmmss suffix.
    // Supply <=29 ASCII characters, with a persisted per-run token to locate the committed job.
    public static string Token(RunRecord run)
    {
        var label = Regex.Replace(run.Definition.JobName, "[^A-Za-z0-9_]", "_");
        if (label.Length == 0) label = "JOB";
        return label[..Math.Min(label.Length, 8)] + "_" + Guid.ParseExact(run.Id, "N").ToString("N")[..20];
    }
    public static string Prefix(RunRecord run) => "BTVY_" + (run.GeneratorJobName ?? throw new InvalidOperationException("Çalışma etiketi yok.")) + "_";
    public static void Validate(JobDefinition job)
    {
        if (string.IsNullOrWhiteSpace(job.JobName) || job.JobName.Length > 128) throw new ArgumentException("Job adı zorunlu ve en fazla 128 karakter olmalı.");
        var packages = new[] {job.MIGPackageName,job.StgPackageName,job.SkeyPackageName,job.LryPackageName,job.EdwPackageName,job.EdwdmPackageName};
        if (packages.Any(p => p?.Length > 300)) throw new ArgumentException("Paket adları en fazla 300 karakter olabilir.");
        foreach (var sql in new[] { job.Sql1, job.Sql2 })
        {
            if (sql?.Length > 4000) throw new ArgumentException("SQL alanları en fazla 4000 karakter olabilir.");
            if (sql?.Contains('"') == true) throw new ArgumentException("Prosedür SQL alanlarında çift tırnağa izin vermiyor.");
        }
        if (packages.All(string.IsNullOrWhiteSpace) && string.IsNullOrWhiteSpace(job.Sql1) && string.IsNullOrWhiteSpace(job.Sql2))
            throw new ArgumentException("En az bir paket veya SQL adımı ekleyin.");
    }
    internal static void AddParameters(SqlCommand cmd, RunRecord run)
    {
        var j = run.Definition;
        foreach (var (name, value, size) in new (string, string?, int)[] {
            ("JobName",run.GeneratorJobName,50),("MIGPackageName",j.MIGPackageName,300),("StgPackageName",j.StgPackageName,300),
            ("SkeyPackageName",j.SkeyPackageName,300),("LryPackageName",j.LryPackageName,300),("Sql1",j.Sql1,4000),
            ("EdwPackageName",j.EdwPackageName,300),("Sql2",j.Sql2,4000),("EdwdmPackageName",j.EdwdmPackageName,300) })
            cmd.Parameters.Add("@" + name, SqlDbType.VarChar, size).Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        // Explicit NULL/bit would skip both STG branches in the supplied procedure.
        cmd.Parameters.Add("@StgFullInitialLoad", SqlDbType.VarChar, 3).Value = j.StgFullInitialLoad == true ? "YES" : "NO";
    }
}
