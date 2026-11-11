using SqlJobRunner;
using Xunit;

public class SsisCorrelationTests
{
    [Theory]
    [InlineData("/ISSERVER \\\"\\SSISDB\\EDW\\EDW\\MAIN.dtsx\\\" /SERVER PASIFIK /ENVREFERENCE 5")]
    [InlineData("/ISSERVER \"\\SSISDB\\EDW\\EDW\\MAIN.dtsx\" /SERVER \"PASIFIK\" /ENVREFERENCE 5")]
    public void Parse_actual_dtexec_package_path_server_and_environment(string command)
    {
        var target=SsisCorrelation.ParseTarget(command);
        Assert.NotNull(target);
        Assert.Equal("PASIFIK",target.Server); Assert.Equal("EDW",target.Folder);
        Assert.Equal("EDW",target.Project); Assert.Equal("MAIN.dtsx",target.Package); Assert.Equal(5,target.ReferenceId);
    }
    [Fact]
    public void No_id_matches_only_one_exact_package_and_time_window_and_never_nearest_run()
    {
        var start=DateTimeOffset.Parse("2026-10-22T07:00:00+03:00");
        var step=new SsisFailedStep(2,"EDW","","",start,start.AddSeconds(10));
        var target=new SsisPackageTarget("PASIFIK","EDW","EDW","MAIN.dtsx",5);
        var candidate=new SsisCandidate(76503,"EDW","EDW","MAIN.dtsx",start.AddMilliseconds(200),start.AddSeconds(1),start.AddSeconds(10),5);
        Assert.True(SsisCorrelation.Matches(candidate,target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { Package="OTHER.dtsx" },target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { ReferenceId=7 },target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { CreatedAt=start.AddMinutes(-1) },target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { FinishedAt=start.AddSeconds(11) },target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { StartedAt=null },target,step));
        Assert.False(SsisCorrelation.Matches(candidate with { Folder="TD" },target,step));
        Assert.Equal(candidate,SsisCorrelation.Unique(new[] {candidate}));
        Assert.Null(SsisCorrelation.Unique(new[] {candidate,candidate with {Id=76504}}));
        Assert.Null(SsisCorrelation.Unique(Array.Empty<SsisCandidate>()));
    }
    [Fact]
    public void Explicit_id_must_still_match_package_and_environment()
    {
        var start=DateTimeOffset.UtcNow;
        var target=new SsisPackageTarget("PASIFIK","EDW","EDW","MAIN.dtsx",5);
        var step=new SsisFailedStep(1,"EDW","","Execution ID: 76503",start,start.AddSeconds(10));
        var candidate=new SsisCandidate(76503,"EDW","EDW","MAIN.dtsx",start,start,start.AddSeconds(9),5);
        Assert.True(SsisCorrelation.Matches(candidate,target,step,76503));
        Assert.False(SsisCorrelation.Matches(candidate,target,step,76504));
        Assert.False(SsisCorrelation.Matches(candidate with {Project="OTHER"},target,step,76503));
    }
}
