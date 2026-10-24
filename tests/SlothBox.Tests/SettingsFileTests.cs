using System.Text.Json;
using SlothBox.Core;
using SlothBox.Server.Settings;

namespace SlothBox.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly TempFolder _temp = new();

    [Fact]
    public void The_first_run_writes_the_defaults_with_a_new_phone_token_that_a_restart_keeps()
    {
        var path = _temp.PathOf("SlothBox", "settings.json");

        var settings = new SettingsFile(path).Current;

        File.Exists(path).ShouldBeTrue();
        settings.Phone.Token.Length.ShouldBeGreaterThanOrEqualTo(20);
        (settings with { Phone = settings.Phone with { Token = "" } }).ShouldBe(new SlothBoxSettings());
        new SettingsFile(path).Current.Phone.Token.ShouldBe(settings.Phone.Token);
    }

    [Fact]
    public void Each_first_run_gets_its_own_token() =>
        new SettingsFile(_temp.PathOf("a", "settings.json")).Current.Phone.Token
            .ShouldNotBe(new SettingsFile(_temp.PathOf("b", "settings.json")).Current.Phone.Token);

    [Fact]
    public void An_existing_file_is_read_and_left_as_it_is()
    {
        var path = _temp.PathOf("settings.json");
        var saved = new SlothBoxSettings
        {
            OutputRoot = @"D:\slothbox-test-output",
            Phone = new PhoneSettings(true, 5081, "synthetic-saved-token", 50)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(saved));
        var written = File.GetLastWriteTimeUtc(path);

        new SettingsFile(path).Current.ShouldBe(saved);
        File.GetLastWriteTimeUtc(path).ShouldBe(written);
    }

    [Fact]
    public void A_file_without_a_token_gets_one_saved_and_keeps_its_other_values()
    {
        var path = _temp.PathOf("settings.json");
        File.WriteAllText(path, """{ "OutputRoot": "D:\\slothbox-test-output" }""");

        var settings = new SettingsFile(path).Current;

        settings.OutputRoot.ShouldBe(@"D:\slothbox-test-output");
        settings.Phone.Token.Length.ShouldBeGreaterThanOrEqualTo(20);
        new SettingsFile(path).Current.ShouldBe(settings);
    }

    [Fact]
    public void A_hand_edited_file_with_comments_trailing_commas_and_camel_case_is_read()
    {
        var path = _temp.PathOf("settings.json");
        File.WriteAllText(path, """
            {
              // where sorted files go
              "outputRoot": "D:\\slothbox-test-output",
              "phone": { "token": "synthetic-hand-written-token", },
            }
            """);

        var settings = new SettingsFile(path).Current;

        (settings.OutputRoot, settings.Phone.Token)
            .ShouldBe((@"D:\slothbox-test-output", "synthetic-hand-written-token"));
    }

    [Fact]
    public void A_broken_file_is_reported_on_every_read_until_it_is_fixed()
    {
        var path = _temp.PathOf("settings.json");
        File.WriteAllText(path, "{ not json");
        var file = new SettingsFile(path);

        Should.Throw<InvalidOperationException>(() => file.Current).Message.ShouldContain(path);
        Should.Throw<InvalidOperationException>(() => file.Current);
        File.WriteAllText(path, """{ "Phone": { "Token": "synthetic-fixed-token" } }""");
        file.Current.Phone.Token.ShouldBe("synthetic-fixed-token");
    }

    [Fact]
    public void A_saved_change_is_current_at_once_and_read_back_after_a_restart()
    {
        var path = _temp.PathOf("SlothBox", "settings.json");
        var file = new SettingsFile(path);
        var token = file.Current.Phone.Token;

        var updated = file.Update(settings => settings with { OutputRoot = @"D:\slothbox-test-output" });

        updated.OutputRoot.ShouldBe(@"D:\slothbox-test-output");
        file.Current.ShouldBe(updated);
        new SettingsFile(path).Current.ShouldBe(updated);
        updated.Phone.Token.ShouldBe(token);
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldBe([path]);
    }

    [Fact]
    public void A_broken_file_is_never_overwritten_by_a_change()
    {
        var path = _temp.PathOf("settings.json");
        File.WriteAllText(path, "{ not json");

        Should.Throw<InvalidOperationException>(() =>
            new SettingsFile(path).Update(settings => settings with { OutputRoot = @"D:\slothbox-test-output" }));
        File.ReadAllText(path).ShouldBe("{ not json");
    }

    [Fact]
    public void New_phone_tokens_are_url_safe_and_differ()
    {
        var first = SettingsFile.NewPhoneToken();

        first.Length.ShouldBeGreaterThanOrEqualTo(20);
        first.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
        SettingsFile.NewPhoneToken().ShouldNotBe(first);
    }

    public void Dispose() => _temp.Dispose();
}
