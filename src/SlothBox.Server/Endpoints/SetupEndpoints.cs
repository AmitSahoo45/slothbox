using SlothBox.Data.Files;
using SlothBox.Server.Api;
using SlothBox.Server.Phone;
using SlothBox.Server.Watching;

namespace SlothBox.Server.Endpoints;

// Spec 8.2 routes of first-run setup and the Settings page (E17): the folder dialog, settings, setup, the phone link and
// ignored files. StubEndpoints maps them at their place in the route list, so the contract keeps its order.
internal static class SetupEndpoints
{
    internal static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/system/pick-folder", async (SetupService setup, CancellationToken ct) =>
                (await setup.PickFolderAsync(ct)).ToResult(""))
            .WithName("PickFolder").Produces<PickFolderResultDto>().ProducesProblem(StatusCodes.Status409Conflict);
        api.MapGet("/settings", (SettingsService settings) => Results.Ok(settings.Get()))
            .WithName("GetSettings").Produces<SettingsDto>();
        api.MapPut("/settings", (UpdateSettingsRequest request, SettingsService settings) =>
                settings.Update(request).ToResult(""))
            .WithName("UpdateSettings").Produces<SettingsDto>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapGet("/setup", async (SetupService setup, CancellationToken ct) => Results.Ok(await setup.GetAsync(ct)))
            .WithName("GetSetup").Produces<SetupDto>();
        api.MapPost("/setup/complete", async (CompleteSetupRequest request, SetupService setup, CancellationToken ct) =>
                (await setup.CompleteAsync(request, ct)).ToResult(""))
            .WithName("CompleteSetup").Produces<SetupDto>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/setup/check-output", async (CheckFolderRequest request, SetupService setup,
                CancellationToken ct) => Results.Ok(await setup.CheckOutputAsync(request.Path, ct)))
            .WithName("CheckOutputFolder").Produces<OutputFolderCheckDto>();
        api.MapGet("/phone", (PhoneLink phone) => Results.Ok(phone.Get()))
            .WithName("GetPhone").Produces<PhoneDto>();
        api.MapPost("/phone/rotate", (PhoneLink phone) => Results.Ok(phone.Rotate()))
            .WithName("RotatePhone").Produces<PhoneDto>();
        api.MapGet("/ignored", async (IgnoredFiles ignored, CancellationToken ct) =>
                Results.Ok((await ignored.ListAsync(ct))
                    .Select(item => new IgnoredFileDto(item.Sha256, item.FileName, item.At, item.Reason)).ToList()))
            .WithName("GetIgnored").Produces<IReadOnlyList<IgnoredFileDto>>();
        api.MapDelete("/ignored/{sha256}", AllowAsync)
            .WithName("AllowIgnored").Produces<MutationResultDto>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Allow again (spec 6.6): the sweep asked for now queues the file where Undo put it back.
    private static async Task<IResult> AllowAsync(string sha256, IgnoredFiles ignored, SweepRequests sweeps,
        CancellationToken ct)
    {
        if (!await ignored.AllowAsync(sha256, ct))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "This file is not on the ignored list.");
        sweeps.Request();
        return Results.Ok(new MutationResultDto(true, null));
    }
}
