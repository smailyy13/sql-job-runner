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
    public void Connection_builder_always_uses_edwtemp_including_old_run_snapshots()
    {
        var store=NewStore();using var connection=new AgentClient(store).Connect(new("fake",Database:"EDWUTIL"));
        var parsed=new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("EDWTEMP",parsed.InitialCatalog);Assert.Equal("fake",parsed.DataSource);
    }
    [Fact]
    public void Database_from_client_is_not_configurable()
    {
        var store=NewStore();store.SetConnection(new("fake",Database:"master"));
        Assert.Equal("EDWTEMP",store.GetConnection()!.Database);
        Assert.Equal("EDWTEMP",Assert.Single(store.RecentConnections()).Database);
        Assert.Equal("EDWTEMP",store.DraftConnection(new("fake",Database:"msdb")).Database);
    }
}
