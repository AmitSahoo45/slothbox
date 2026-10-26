using System.Globalization;
using System.Text;
using SlothBox.Core;
using SlothBox.Data.Receipts;
using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// Spec 7.4.5 and 8.2: a month's receipts with totals per currency (nothing is converted) and per category, and the
// same month as CSV. Mapped by StubEndpoints at their place in the route list.
internal static class ReceiptEndpoints
{
    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/receipts", async (string? month, ReceiptQueries receipts, CancellationToken ct) =>
                MonthOf(month) is { } first
                    ? Results.Ok(ToDto(first, await receipts.MonthAsync(first, ct)))
                    : BadMonth())
            .WithName("GetReceipts").Produces<ReceiptsDto>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/receipts/export.csv", async (string? month, ReceiptQueries receipts, CancellationToken ct) =>
                MonthOf(month) is { } first
                    ? Results.File(Csv(await receipts.MonthAsync(first, ct)), "text/csv",
                        $"slothbox-receipts-{first:yyyy-MM}.csv")
                    : BadMonth())
            .WithName("ExportReceipts").Produces<string>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    // "2026-10"; the current local month when left out; null when it is not a month.
    private static DateOnly? MonthOf(string? month)
    {
        if (string.IsNullOrEmpty(month))
            return new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
        return DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var first) ? first : null;
    }

    private static IResult BadMonth() => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: "Choose a month like 2026-10.");

    // Bars are sorted by amount (spec 7.4.5); the main currency comes first in the totals.
    private static ReceiptsDto ToDto(DateOnly month, IReadOnlyList<ReceiptRow> rows) => new(
        month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        [
            .. rows.Select(r => new ReceiptRowDto(r.FileId, r.Vendor, r.Date, r.Amount, r.Currency, r.PaymentMethod,
                r.CategoryId, r.Category))
        ],
        [
            .. rows.GroupBy(r => r.Currency)
                .Select(g => new CurrencyTotalDto(g.Key, g.Sum(r => r.Amount), g.Count()))
                .OrderByDescending(t => t.Count).ThenBy(t => t.Currency, StringComparer.Ordinal)
        ],
        [
            .. rows.GroupBy(r => (r.CategoryId, r.Category, r.Currency))
                .Select(g => new CategoryTotalDto(g.Key.CategoryId ?? 0, g.Key.Category, g.Key.Currency,
                    g.Sum(r => r.Amount)))
                .OrderByDescending(t => t.Amount).ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
        ]);

    // UTF-8 with a byte order mark, so spreadsheet apps read vendor names right.
    private static byte[] Csv(IReadOnlyList<ReceiptRow> rows)
    {
        var csv = new StringBuilder("Date,Vendor,Category,Paid by,Amount,Currency,File\r\n");
        foreach (var r in rows)
            csv.Append(string.Join(',', r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Cell(r.Vendor ?? ""),
                Cell(r.Category), PaidBy(r.PaymentMethod), r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                Cell(r.Currency), Cell(r.FileName))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    // RFC 4180 quoting. Vendors come from Gemma, so a cell a spreadsheet would run as a formula starts with an
    // apostrophe instead.
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string PaidBy(PaymentMethod method) => method switch
    {
        PaymentMethod.Upi => "UPI",
        PaymentMethod.Card => "Card",
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Netbanking => "Net banking",
        _ => ""
    };
}
