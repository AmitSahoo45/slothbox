using SlothBox.Server.Worker;

namespace SlothBox.Tests.Worker;

public sealed class WorkerStatsTests
{
    [Fact]
    public void Nothing_filed_today_has_no_average() => new WorkerStats().AverageSecondsToday.ShouldBeNull();

    [Fact]
    public void The_average_is_over_the_files_filed_today()
    {
        var stats = new WorkerStats();

        stats.Record(TimeSpan.FromSeconds(2));
        stats.Record(TimeSpan.FromSeconds(4.5));

        stats.AverageSecondsToday.ShouldBe(3.25);
    }

    [Fact]
    public void A_new_day_starts_a_new_average()
    {
        var day = new DateOnly(2026, 10, 22);
        var stats = new WorkerStats(() => day);
        stats.Record(TimeSpan.FromSeconds(9));

        day = day.AddDays(1);

        stats.AverageSecondsToday.ShouldBeNull();
        stats.Record(TimeSpan.FromSeconds(3));
        stats.AverageSecondsToday.ShouldBe(3);
    }
}
