using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Server.Settings;
using SlothBox.Server.Watching;
using SlothBox.Server.Worker;
using SlothBox.Tests.Api.Dashboard;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Setup;

// First-run setup (spec 4.3 step 5, 7.4.1, 8.2) over a real database and temp folders.
[Collection(PostgresCollection.Name)]
public sealed class SetupEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A first run: no output folder yet. services: what the App would add.
    private async Task<DashboardHarness> FirstRunAsync(Action<IServiceCollection>? services = null)
    {
        var h = await DashboardHarness.CreateAsync(postgres, services);
        h.Pipeline.Settings.Current = new SlothBoxSettings();
        return h;
    }

    private static string NewFolder(DashboardHarness h, string[] parts, params string[] files)
    {
        var path = h.Moves.Temp.PathOf(parts);
        Directory.CreateDirectory(path);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(path, file), $"content of {file}");
        return path;
    }

    private static async Task<SetupDto> CompleteAsync(DashboardHarness h, CompleteSetupRequest request)
    {
        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/complete", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<SetupDto>();
    }

    private static async Task<OutputFolderCheckDto> CheckOutputAsync(DashboardHarness h, string path)
    {
        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/check-output", new CheckFolderRequest(path));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<OutputFolderCheckDto>();
    }

    [Fact]
    public async Task Setup_starts_incomplete_with_the_default_output_folder_and_the_known_folders_suggested()
    {
        var known = new FakeKnownFolders();
        await using var h = await FirstRunAsync(services => services.AddSingleton<IKnownFolders>(known));
        known.Downloads = NewFolder(h, ["Users", "me", "Downloads"]);
        known.Screenshots = NewFolder(h, ["Users", "me", "Dropbox", "Screenshots"]);

        var setup = await h.GetAsync<SetupDto>("/api/setup");

        (setup.Complete, setup.OutputRoot, setup.NativePickerAvailable).ShouldBe((false, @"D:\SlothBox", false));
        setup.SuggestedFolders.ShouldBe([
            new InitialFolderDto(known.Downloads, "Downloads", FolderMode.Move),
            new InitialFolderDto(known.Screenshots, "Screenshots", FolderMode.Copy)
        ]);
        (setup.Status.SetupComplete, setup.Status.Database.Ready).ShouldBe((false, true));
    }

    [Fact]
    public async Task A_known_folder_that_is_missing_or_already_watched_is_not_suggested()
    {
        var known = new FakeKnownFolders();
        await using var h = await FirstRunAsync(services => services.AddSingleton<IKnownFolders>(known));
        known.Downloads = h.Moves.Temp.PathOf("Users", "me", "Missing downloads");
        known.Screenshots = NewFolder(h, ["Users", "me", "Screenshots"]);
        await h.Pipeline.Folders.SaveAsync(new WatchedFolder(0, known.Screenshots, "Screenshots", FolderMode.Move,
            false, true), Ct);

        (await h.GetAsync<SetupDto>("/api/setup")).SuggestedFolders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Finishing_setup_creates_the_output_folder_watches_the_chosen_folders_and_leaves_their_files()
    {
        await using var h = await FirstRunAsync();
        var downloads = NewFolder(h, ["Users", "me", "Downloads"], "old-bill.pdf");
        var camera = NewFolder(h, ["Users", "me", "Dropbox", "Camera"], "IMG_2041.jpg", "IMG_2042.jpg");
        var output = h.Moves.Temp.PathOf("Sorted", "SlothBox");

        var setup = await CompleteAsync(h, new CompleteSetupRequest(output + Path.DirectorySeparatorChar,
        [
            new InitialFolderDto(downloads, "Downloads", FolderMode.Move),
            new InitialFolderDto(camera, "", FolderMode.Move)
        ], false));

        (setup.Complete, setup.OutputRoot, setup.Status.SetupComplete).ShouldBe((true, output, true));
        setup.SuggestedFolders.ShouldBeEmpty();
        h.Pipeline.Settings.Current.OutputRoot.ShouldBe(output);
        Directory.Exists(Path.Combine(output, "Inbox")).ShouldBeTrue();
        var folders = await h.GetAsync<List<FolderDto>>("/api/folders");
        folders.Select(f => (f.Path, f.Label, f.Mode, f.IsCloud, f.Enabled, f.BaselineCount)).ShouldBe([
            (downloads, "Downloads", FolderMode.Move, false, true, 1),
            (camera, "Camera", FolderMode.Copy, true, true, 2)
        ], ignoreOrder: true);
        // Spec 4.5: first-run setup always leaves the files already there.
        (await h.Service<FolderSweep>().RunAsync(Ct)).ShouldBe(0);
        File.Exists(Path.Combine(downloads, "old-bill.pdf")).ShouldBeTrue();
        (await h.Service<IAppState>().GetAsync("setup_completed_at", Ct)).ShouldNotBeNullOrWhiteSpace();
        (h.Service<WorkerState>().Paused, setup.Status.Paused).ShouldBe((false, false));
        (await h.GetAsync<SetupDto>("/api/setup")).Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task Finish_anyway_finishes_with_the_queue_paused_until_the_user_resumes()
    {
        await using var h = await FirstRunAsync();

        var setup = await CompleteAsync(h, new CompleteSetupRequest(h.Moves.Temp.PathOf("SlothBox"), [], true));

        (setup.Complete, setup.Status.Paused, setup.Status.PauseReason).ShouldBe((true, true, PauseReason.User));
        h.Service<WorkerState>().Reason.ShouldBe(PauseReason.User);
    }

    [Fact]
    public async Task A_refused_output_folder_is_reported_and_nothing_is_saved()
    {
        await using var h = await FirstRunAsync();
        var downloads = NewFolder(h, ["Users", "me", "Downloads"]);
        var output = Path.Combine(downloads, "SlothBox");

        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/complete", new CompleteSetupRequest(output,
            [new InitialFolderDto(downloads, "Downloads", FolderMode.Move)], false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "SlothBox watches this folder. Pick another place for sorted files.");
        h.Pipeline.Settings.Current.OutputRoot.ShouldBeEmpty();
        Directory.Exists(output).ShouldBeFalse();
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_folder_is_named_and_nothing_is_saved()
    {
        await using var h = await FirstRunAsync();
        var downloads = NewFolder(h, ["Users", "me", "Downloads"]);
        var output = h.Moves.Temp.PathOf("SlothBox");

        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/complete", new CompleteSetupRequest(output,
        [
            new InitialFolderDto(downloads, "Downloads", FolderMode.Move),
            new InitialFolderDto(h.Moves.Temp.PathOf("Users", "me", "Gone"), "Old scans", FolderMode.Move)
        ], false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe("Old scans: Folder not found.");
        (h.Pipeline.Settings.Current.OutputRoot, Directory.Exists(output)).ShouldBe(("", false));
        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_chosen_inside_the_output_folder_is_refused()
    {
        await using var h = await FirstRunAsync();
        var output = h.Moves.Temp.PathOf("SlothBox");
        var inside = NewFolder(h, ["SlothBox", "Scans"]);

        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/complete", new CompleteSetupRequest(output,
            [new InitialFolderDto(inside, "Scans", FolderMode.Move)], false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "Scans: This is SlothBox's output folder. Watching it would make SlothBox refile its own files.");
    }

    [Fact]
    public async Task An_empty_folder_entry_is_refused()
    {
        await using var h = await FirstRunAsync();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            outputRoot = h.Moves.Temp.PathOf("SlothBox"), folders = new object?[] { null }, finishAnyway = false
        });

        using var response = await h.SendJsonAsync(HttpMethod.Post, "/api/setup/complete", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe("Choose a folder for each entry.");
    }

    // Spec 11: settings.json broken since startup. Setup is not finished, and Finish anyway's pause is lifted again.
    [Fact]
    public async Task A_settings_file_that_cannot_be_saved_leaves_setup_unfinished_and_the_queue_running()
    {
        await using var h = await FirstRunAsync(services =>
            services.AddSingleton<ISettingsStore>(new UnsavableSettings(new SlothBoxSettings())));

        using var response = await h.SendAsync(HttpMethod.Post, "/api/setup/complete",
            new CompleteSetupRequest(h.Moves.Temp.PathOf("SlothBox"), [], true));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            $"SlothBox could not save its settings: {UnsavableSettings.Problem}");
        h.Service<WorkerState>().Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task Setup_can_be_finished_only_once()
    {
        await using var h = await FirstRunAsync();
        var output = h.Moves.Temp.PathOf("SlothBox");
        await CompleteAsync(h, new CompleteSetupRequest(output, [], false));

        using var again = await h.SendAsync(HttpMethod.Post, "/api/setup/complete",
            new CompleteSetupRequest(h.Moves.Temp.PathOf("Elsewhere"), [], false));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "Setup is already finished. The output folder can't be changed in this version.");
        h.Pipeline.Settings.Current.OutputRoot.ShouldBe(output);
    }

    [Fact]
    public async Task Finishing_again_after_a_failed_try_keeps_one_row_per_folder()
    {
        await using var h = await FirstRunAsync();
        var downloads = NewFolder(h, ["Users", "me", "Downloads"]);
        // An earlier try saved the folder, then failed before the output folder was set.
        await h.Pipeline.Folders.AddAsync(new WatchedFolder(0, downloads, "Downloads", FolderMode.Move, false, true),
            [], Ct);

        await CompleteAsync(h, new CompleteSetupRequest(h.Moves.Temp.PathOf("SlothBox"),
        [
            new InitialFolderDto(downloads, "Downloads", FolderMode.Move),
            new InitialFolderDto(downloads + Path.DirectorySeparatorChar, "Downloads", FolderMode.Move)
        ], false));

        (await h.GetAsync<List<FolderDto>>("/api/folders")).ShouldHaveSingleItem().Path.ShouldBe(downloads);
    }

    [Fact]
    public async Task The_output_folder_check_answers_the_rules_before_anything_is_saved()
    {
        await using var h = await FirstRunAsync();
        var downloads = NewFolder(h, ["Users", "me", "Downloads"]);
        await h.Pipeline.Folders.SaveAsync(new WatchedFolder(0, downloads, "Downloads", FolderMode.Move, false, true),
            Ct);

        (await CheckOutputAsync(h, h.Moves.Temp.PathOf("Sorted"))).ShouldBe(new OutputFolderCheckDto(true, null));
        (await CheckOutputAsync(h, Path.Combine(downloads, "Sorted"))).ShouldBe(new OutputFolderCheckDto(false,
            "SlothBox watches this folder. Pick another place for sorted files."));
        (await CheckOutputAsync(h, "Sorted")).ShouldBe(new OutputFolderCheckDto(false,
            "Use a full folder path, like D:\\SlothBox."));
        Directory.Exists(h.Moves.Temp.PathOf("Sorted")).ShouldBeFalse();
        h.Pipeline.Settings.Current.OutputRoot.ShouldBeEmpty();
    }
}

// Setup while the database is down, and the folder dialog: no database needed.
public sealed class SetupWithoutDatabaseTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_password_variable_is_named_by_the_database_check()
    {
        await using var server = new ApiServer(services => services.AddSingleton(new DatabasePassword(null)));

        var setup = await server.GetAsync<SetupDto>("/api/setup");

        setup.Status.Database.ShouldBe(new ServiceStatusDto(false, "Set SLOTHBOX_DB_PASSWORD (see README setup)."));
        (setup.Complete, setup.SuggestedFolders.Count).ShouldBe((false, 0));
    }

    [Fact]
    public async Task With_the_password_set_the_database_check_says_it_is_not_answering()
    {
        await using var server = new ApiServer(services =>
            services.AddSingleton(new DatabasePassword("synthetic-password")));

        (await server.GetAsync<SetupDto>("/api/setup")).Status.Database
            .ShouldBe(new ServiceStatusDto(false, "The database is not answering."));
    }

    [Fact]
    public async Task Finishing_setup_waits_for_the_database_and_saves_nothing()
    {
        await using var server = new ApiServer();
        var output = _temp.PathOf("SlothBox");

        using var response = await server.SendAsync(HttpMethod.Post, "/api/setup/complete",
            new CompleteSetupRequest(output, [], true));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "The database is offline, so SlothBox can't save your folders yet. Start Docker Desktop, then try again.");
        (server.Settings.Current.OutputRoot, Directory.Exists(output)).ShouldBe(("", false));
        server.Services.GetRequiredService<WorkerState>().Paused.ShouldBeFalse();
    }

    [Fact]
    public async Task The_output_folder_check_works_while_the_database_is_down()
    {
        await using var server = new ApiServer();

        using var response = await server.SendAsync(HttpMethod.Post, "/api/setup/check-output",
            new CheckFolderRequest(_temp.PathOf("Sorted")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadAsync<OutputFolderCheckDto>()).ShouldBe(new OutputFolderCheckDto(true, null));
    }

    [Fact]
    public async Task Without_the_app_there_is_no_folder_dialog()
    {
        await using var server = new ApiServer();

        (await server.GetAsync<SetupDto>("/api/setup")).NativePickerAvailable.ShouldBeFalse();
        using var response = await server.SendAsync(HttpMethod.Post, "/api/system/pick-folder");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "The folder dialog isn't available. Type or paste the folder path instead.");
    }

    [Fact]
    public async Task The_folder_dialog_returns_the_chosen_folder_or_null_when_cancelled()
    {
        var dialogs = new FakeDialogs { Answer = _temp.PathOf("Scans") };
        await using var server = new ApiServer(services => services.AddSingleton<INativeDialogs>(dialogs));

        (await server.GetAsync<SetupDto>("/api/setup")).NativePickerAvailable.ShouldBeTrue();
        using (var picked = await server.SendAsync(HttpMethod.Post, "/api/system/pick-folder"))
        {
            picked.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await picked.ReadAsync<PickFolderResultDto>()).ShouldBe(new PickFolderResultDto(_temp.PathOf("Scans")));
        }
        dialogs.Answer = null;
        using (var cancelled = await server.SendAsync(HttpMethod.Post, "/api/system/pick-folder"))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await cancelled.ReadAsync<PickFolderResultDto>()).ShouldBe(new PickFolderResultDto(null));
        }
    }

    [Fact]
    public async Task A_failing_folder_dialog_falls_back_to_a_typed_path()
    {
        var dialogs = new FakeDialogs { Failure = new InvalidOperationException("The dialog could not open.") };
        await using var server = new ApiServer(services => services.AddSingleton<INativeDialogs>(dialogs));

        using var response = await server.SendAsync(HttpMethod.Post, "/api/system/pick-folder");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadAsync<ProblemDetails>()).Title.ShouldBe(
            "The folder dialog isn't available. Type or paste the folder path instead.");
    }

    public void Dispose() => _temp.Dispose();
}
