using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlothBox.Tests.Api;

// Reads API answers the way the UI does: camelCase, enums as snake_case strings (spec 8.1).
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Options, TestContext.Current.CancellationToken))!;
}
