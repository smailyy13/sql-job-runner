using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlJobRunner;
using Xunit;

public class ConnectionDefaultsTests
{
    private static Store NewStore() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["DataDirectory"]=Path.Combine(Path.GetTempPath(),"SqlJobRunnerTests",Guid.NewGuid().ToString("N")) }).Build());
    [Fact]
    public void New_connection_defaults_to_optional_encryption_and_trusted_certificate()
    {
        var input=new ConnectionInput("fake");Assert.False(input.Encrypt);Assert.True(input.TrustServerCertificate);
        var store=NewStore();store.SetConnection(input with {TrustServerCertificate=false});
        Assert.False(store.GetConnection()!.TrustServerCertificate);
    }
    [Fact]
    public void Connection_builder_always_uses_master_including_old_run_snapshots()
    {
        var store=NewStore();using var connection=new AgentClient(store).Connect(new("fake",Database:"EDWTEMP"));
        var parsed=new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("master",parsed.InitialCatalog);Assert.Equal("fake",parsed.DataSource);
    }
    [Fact]
    public void Database_from_client_is_not_configurable()
    {
        var store=NewStore();store.SetConnection(new("fake",Database:"EDWTEMP"));
        Assert.Equal("master",store.GetConnection()!.Database);
        Assert.Equal("master",Assert.Single(store.RecentConnections()).Database);
        Assert.Equal("master",store.DraftConnection(new("fake",Database:"msdb")).Database);
    }
    [Fact]
    public void Recent_connection_saved_under_old_default_is_merged_and_forgotten()
    {
        var store=NewStore();store.SetConnection(new("fake"));
        using (var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(store.DataDirectory,"jobs.db")}"))
        {
            db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="INSERT INTO recent_connections(id,body) VALUES('legacy-edwtemp-id',$body)";
            cmd.Parameters.AddWithValue("$body",System.Text.Json.JsonSerializer.Serialize(new ConnectionInput("FAKE",Database:"EDWTEMP"),Store.Json));cmd.ExecuteNonQuery();
        }
        var recent=Assert.Single(store.RecentConnections());
        store.ForgetConnection(recent.Id);
        Assert.Empty(store.RecentConnections());
    }
}
