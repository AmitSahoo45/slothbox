using System.Text.Json;

namespace SlothBox.Data;

// The stored text for an enum value, matching SnakeCaseEnumConverter, for raw SQL parameters.
internal static class DbValue
{
    public static string Of<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
