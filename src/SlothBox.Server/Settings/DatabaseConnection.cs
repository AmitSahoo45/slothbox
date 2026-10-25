using Npgsql;
using SlothBox.Core;

namespace SlothBox.Server.Settings;

// Spec 11: the password comes only from the user-level SLOTHBOX_DB_PASSWORD variable, is added in memory and is
// never written to settings.json or logged.
public static class DatabaseConnection
{
    public const string PasswordVariable = "SLOTHBOX_DB_PASSWORD";

    public static string For(SlothBoxSettings settings, string? password) =>
        new NpgsqlConnectionStringBuilder(settings.Database.ConnectionString) { Password = password }.ConnectionString;
}

// The password as this process sees it, for the Database check (spec 11). Tests pass their own.
public sealed record DatabasePassword(string? Value)
{
    public static DatabasePassword FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(DatabaseConnection.PasswordVariable));
}
