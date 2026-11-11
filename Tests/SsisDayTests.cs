using SqlJobRunner;
using Xunit;

public class SsisDayTests
{
    [Fact]
    public void Day_uses_Turkey_date_even_when_timestamp_is_UTC()
    {
        Assert.Equal(new DateOnly(2026, 10, 22), SsisDays.Of(DateTimeOffset.Parse("2026-10-21T21:00:00Z")));
        Assert.Equal(new DateOnly(2026, 10, 21), SsisDays.Of(DateTimeOffset.Parse("2026-10-21T20:59:59Z")));
    }

    [Fact]
    public void Range_includes_whole_day_and_excludes_next_midnight()
    {
        var (from, before) = SsisDays.Bounds(new DateOnly(2026, 10, 22));
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T00:00:00+03:00"), from);
        Assert.Equal(DateTimeOffset.Parse("2026-10-23T00:00:00+03:00"), before);
        Assert.Equal(TimeSpan.FromHours(24), before - from);
    }
}
