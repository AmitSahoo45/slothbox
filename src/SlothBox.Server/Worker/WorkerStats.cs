namespace SlothBox.Server.Worker;

// Today's average seconds per file, for the status block (spec 12). Kept in memory: it starts again with the app.
public sealed class WorkerStats(Func<DateOnly>? today = null)
{
    private readonly Func<DateOnly> _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _count;
    private double _seconds;

    public void Record(TimeSpan duration)
    {
        lock (_gate)
        {
            StartTodayIfNew();
            _count++;
            _seconds += duration.TotalSeconds;
        }
    }

    public double? AverageSecondsToday
    {
        get
        {
            lock (_gate)
            {
                StartTodayIfNew();
                return _count == 0 ? null : Math.Round(_seconds / _count, 2);
            }
        }
    }

    private void StartTodayIfNew()
    {
        var day = _today();
        if (day == _day)
            return;
        (_day, _count, _seconds) = (day, 0, 0);
    }
}
