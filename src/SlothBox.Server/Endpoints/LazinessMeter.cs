using SlothBox.Core;
using SlothBox.Data.Dashboard;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// Spec 6.8: the meter counts each file's first filing, in the laptop's local time. Files in Needs review count, as
// SlothBox moved them out of the way; duplicates are counted as caught; undone files are left out.
public sealed class LazinessMeter(DashboardQueries queries, ISettingsStore settings)
{
    public static readonly string[] Ranges = ["today", "week", "all"];

    public async Task<LazinessStatsDto> GetAsync(string range, CancellationToken ct)
    {
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateTimeOffset? start = range switch
        {
            "today" => Local(today),
            "week" => Local(monday),
            _ => null
        };
        // The bars always show this week, so at least this week is read.
        var filings = await queries.FirstFilingsAsync(start is null ? null : Local(monday), ct);
        var filed = filings.Where(filing => filing.Status is FileStatus.Filed or FileStatus.Review).ToList();
        var inRange = filed.Where(filing => start is null || filing.At >= start).ToList();
        var receipts = inRange.Where(filing => filing is { Type: FileKind.Receipt, Status: FileStatus.Filed }).ToList();
        var laziness = settings.Current.Laziness;

        return new LazinessStatsDto(
            inRange.Count,
            // Summed as long: Settings accepts any whole number of seconds (spec 7.4.8), and an int sum would overflow.
            inRange.Sum(filing => (long)Seconds(filing.Type, laziness)) / 60.0,
            receipts.Count,
            receipts.Where(receipt => receipt is { Amount: not null, Currency: not null })
                .GroupBy(receipt => receipt.Currency!)
                .Select(currency => new CurrencyTotalDto(currency.Key, currency.Sum(receipt => receipt.Amount!.Value),
                    currency.Count()))
                .OrderBy(total => total.Currency, StringComparer.Ordinal).ToList(),
            filings.Count(filing => filing.Status == FileStatus.Duplicate && (start is null || filing.At >= start)),
            Enumerable.Range(0, 7).Select(offset => DateOnly.FromDateTime(monday.AddDays(offset)))
                .Select(day => new DayCountDto(day,
                    filed.Count(filing => DateOnly.FromDateTime(filing.At.LocalDateTime) == day)))
                .ToList(),
            laziness);
    }

    private static int Seconds(FileKind type, LazinessSettings laziness) => type switch
    {
        FileKind.Receipt => laziness.ReceiptSeconds,
        FileKind.Screenshot => laziness.ScreenshotSeconds,
        FileKind.Document => laziness.DocumentSeconds,
        _ => laziness.OtherSeconds
    };

    private static DateTimeOffset Local(DateTime day) => new(day, TimeZoneInfo.Local.GetUtcOffset(day));
}
