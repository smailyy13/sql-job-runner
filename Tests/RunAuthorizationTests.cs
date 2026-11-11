using SqlJobRunner;
using Xunit;

public class RunAuthorizationTests
{
    [Fact]
    public void Login_connection_is_reusable_and_invalid_tokens_are_rejected()
    {
        var session=new ConnectionSession(); var now=DateTimeOffset.UtcNow;
        var settings=new RequestSettings("PASIFIK"); var employee=new ConnectionInput("PASIFIK",IntegratedSecurity:false,UserName:"runner");
        Assert.Null(session.GetConnection(null,settings,now));
        var token=session.Authorize(employee,settings,now);
        Assert.Null(session.GetConnection("wrong",settings,now));
        Assert.Equal(employee,session.GetConnection(token,settings,now));
        Assert.Equal(employee,session.GetConnection(token,settings,now.AddHours(2)));
    }
    [Fact]
    public void Repeated_background_reads_do_not_extend_the_three_hour_idle_timeout()
    {
        var session=new ConnectionSession(); var now=DateTimeOffset.UtcNow;
        var settings=new RequestSettings(); var token=session.Authorize(new("PASIFIK"),settings,now);
        for(var minute=0;minute<180;minute++)
        {
            Assert.True(session.IsActive(settings,now.AddMinutes(minute)));
            Assert.NotNull(session.GetConnection(token,settings,now.AddMinutes(minute)));
        }
        Assert.False(session.IsActive(settings,now.AddHours(3)));
        Assert.Null(session.GetConnection(token,settings,now.AddHours(3)));
        Assert.False(session.Touch(token,settings,now.AddHours(3)));
    }
    [Fact]
    public void Successful_activity_extends_timeout_but_wrong_tokens_and_expired_sessions_do_not()
    {
        var session=new ConnectionSession(); var now=DateTimeOffset.UtcNow; var settings=new RequestSettings();
        var token=session.Authorize(new("PASIFIK"),settings,now);
        Assert.False(session.Touch("wrong",settings,now.AddHours(2)));
        Assert.True(session.Touch(token,settings,now.AddHours(2)));
        Assert.True(session.IsActive(settings,now.AddHours(4)));
        Assert.False(session.IsActive(settings,now.AddHours(5)));
        Assert.False(session.Touch(token,settings,now.AddHours(5)));
    }
    [Fact]
    public void Environment_change_or_new_login_revokes_old_session()
    {
        var session=new ConnectionSession(); var now=DateTimeOffset.UtcNow; var settings=new RequestSettings();
        var oldToken=session.Authorize(new("PASIFIK"),settings,now);
        var newToken=session.Authorize(new("PASIFIK",UserName:"new"),settings,now);
        Assert.Null(session.GetConnection(oldToken,settings,now));
        Assert.NotNull(session.GetConnection(newToken,settings,now));
        Assert.Null(session.GetConnection(newToken,new("OTHER"),now));
        Assert.Null(session.GetConnection(newToken,settings,now));
    }
}
