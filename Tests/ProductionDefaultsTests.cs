using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using SqlJobRunner;
using Xunit;

public class ProductionDefaultsTests
{
    private static Store Open(string directory) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["DataDirectory"] = directory }).Build());
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "SqlJobRunnerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Execution_defaults_to_production_and_uses_settings_for_next_run_only()
    {
        var store = Open(DirectoryPath());
        var connection = store.GetExecutionConnection();
        Assert.Equal("PASIFIK", connection.Server); Assert.True(connection.IntegratedSecurity);
        store.SetConnection(new(@"SRVDEV\PASIFIK"));
        Assert.Equal("PASIFIK", store.GetExecutionConnection().Server);
        var first = store.Execute(new() {JobName="First", Sql1="SELECT 1"}, Guid.NewGuid().ToString(), 101);
        store.SetRequestSettings(new("OTHER", Encrypt:true));
        var next = store.GetExecutionConnection();
        Assert.Equal("OTHER", next.Server); Assert.True(next.Encrypt); Assert.True(next.IntegratedSecurity);
        Assert.Contains("PASIFIK", first.ConnectionSnapshot!);
        Assert.DoesNotContain("OTHER", first.ConnectionSnapshot!);
    }

    [Fact]
    public void Legacy_default_migrates_once_and_later_explicit_choice_is_retained()
    {
        var directory = DirectoryPath(); var first = Open(directory);
        first.SetRequestSettings(new(@"SRVDEV\PASIFIK"));
        using(var db = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(directory,"jobs.db")}.ToString()))
        {
            db.Open(); using var cmd=db.CreateCommand();
            cmd.CommandText="DELETE FROM local_migrations WHERE name='production-default-v1'"; cmd.ExecuteNonQuery();
        }
        var upgraded = Open(directory);
        Assert.Equal("PASIFIK", upgraded.GetRequestSettings().Server);
        upgraded.SetRequestSettings(new(@"SRVDEV\PASIFIK"));
        Assert.Equal(@"SRVDEV\PASIFIK", Open(directory).GetRequestSettings().Server);
        Assert.Equal(@"SRVDEV\PASIFIK", new SsisErrorReader(upgraded).Server);
    }
}
