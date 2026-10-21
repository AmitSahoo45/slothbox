using Npgsql;
using SlothBox.Core;
using SlothBox.Server.Settings;

namespace SlothBox.Tests;

public sealed class DatabaseConnectionTests
{
    [Fact]
    public void The_password_is_added_to_the_settings_connection_string_only_in_memory()
    {
        var settings = new SlothBoxSettings();

        var connection = new NpgsqlConnectionStringBuilder(
            DatabaseConnection.For(settings, "synthetic-test-password"));

        (connection.Host, connection.Port, connection.Database, connection.Username, connection.Password)
            .ShouldBe(("127.0.0.1", 5433, "slothbox", "slothbox", "synthetic-test-password"));
        settings.Database.ConnectionString.ShouldNotContain("synthetic-test-password");
    }

    [Fact]
    public void Without_the_variable_there_is_no_password() =>
        new NpgsqlConnectionStringBuilder(DatabaseConnection.For(new SlothBoxSettings(), null)).Password.ShouldBeNull();
}
