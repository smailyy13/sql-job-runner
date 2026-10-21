using System.Data;
using Microsoft.Data.SqlClient;
using SqlJobRunner;
using Xunit;

public class GeneratorContractTests
{
    [Fact]
    public void Generated_name_fits_proc_varchar_50_and_is_unique_and_sql_safe()
    {
        var j = new JobDefinition { JobName = "çok uzun ' job adı [SQL] ---" };
        var a = new RunRecord { Definition = j }; var b = new RunRecord { Definition = j };
        a.GeneratorJobName = GeneratorContract.Token(a); b.GeneratorJobName = GeneratorContract.Token(b);
        Assert.NotEqual(a.GeneratorJobName,b.GeneratorJobName);
        Assert.Matches("^[A-Za-z0-9_]{1,29}$",a.GeneratorJobName);
        Assert.True((GeneratorContract.Prefix(a) + "20261016_120000").Length <= 50);
        Assert.Equal(a.GeneratorJobName,GeneratorContract.Token(a));
    }
    [Theory]
    [InlineData(true,"YES")]
    [InlineData(false,"NO")]
    [InlineData(null,"NO")]
    public void Exact_varchar_signature_and_yes_no_stg_flag(bool? full,string expected)
    {
        var run = new RunRecord { Definition = new JobDefinition { StgFullInitialLoad = full, Sql2 = "SELECT 'x';" }, GeneratorJobName = "job_token" };
        using var cmd = new SqlCommand(); GeneratorContract.AddParameters(cmd,run);
        Assert.Equal(10,cmd.Parameters.Count);
        foreach (SqlParameter p in cmd.Parameters) Assert.Equal(SqlDbType.VarChar,p.SqlDbType);
        Assert.Equal(50,cmd.Parameters["@JobName"].Size); Assert.Equal("job_token",cmd.Parameters["@JobName"].Value);
        Assert.Equal(300,cmd.Parameters["@StgPackageName"].Size); Assert.Equal(DBNull.Value,cmd.Parameters["@StgPackageName"].Value);
        Assert.Equal(4000,cmd.Parameters["@Sql2"].Size); Assert.Equal("SELECT 'x';",cmd.Parameters["@Sql2"].Value);
        Assert.Equal(expected,cmd.Parameters["@StgFullInitialLoad"].Value);
    }
    [Fact]
    public void Procedure_constraints_are_checked_before_submission()
    {
        var valid = new JobDefinition { JobName = "job", Sql1 = "SELECT 'allowed';" };
        GeneratorContract.Validate(valid);
        Assert.Throws<ArgumentException>(() => GeneratorContract.Validate(valid with { Sql1 = "SELECT \"bad\";" }));
        Assert.Throws<ArgumentException>(() => GeneratorContract.Validate(valid with { Sql1 = new string('x',4001) }));
        Assert.Throws<ArgumentException>(() => GeneratorContract.Validate(valid with { StgPackageName = new string('x',301) }));
        Assert.Throws<ArgumentException>(() => GeneratorContract.Validate(valid with { Sql1 = null }));
    }
}
