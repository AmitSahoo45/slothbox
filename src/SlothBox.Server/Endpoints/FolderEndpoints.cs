using SlothBox.Server.Api;
using SlothBox.Server.Watching;

namespace SlothBox.Server.Endpoints;

// Spec 8.2 folder routes. StubEndpoints maps them at their place in the route list, so the contract keeps its order.
internal static class FolderEndpoints
{
    private const string Unknown = "SlothBox has no folder with this id.";

    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/folders", async (FolderService folders, CancellationToken ct) =>
                Results.Ok(await folders.ListAsync(ct)))
            .WithName("GetFolders").Produces<IReadOnlyList<FolderDto>>();
        api.MapPost("/folders/check", async (CheckFolderRequest request, FolderService folders, CancellationToken ct) =>
                Results.Ok(await folders.CheckAsync(request.Path, ct)))
            .WithName("CheckFolder").Produces<FolderCheckDto>();
        api.MapPost("/folders", async (CreateFolderRequest request, FolderService folders, CancellationToken ct) =>
                (await folders.AddAsync(request, ct)).ToResult(Unknown))
            .WithName("CreateFolder").Produces<FolderDto>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapPatch("/folders/{id:int}",
                async (int id, UpdateFolderRequest request, FolderService folders, CancellationToken ct) =>
                    (await folders.UpdateAsync(id, request, ct)).ToResult(Unknown))
            .WithName("UpdateFolder").Produces<FolderDto>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/folders/{id:int}/tidy-existing", async (int id, FolderService folders, CancellationToken ct) =>
                (await folders.TidyAsync(id, ct)).ToResult(Unknown))
            .WithName("TidyExisting").Produces<TidyResultDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapDelete("/folders/{id:int}", async (int id, FolderService folders, CancellationToken ct) =>
                (await folders.RemoveAsync(id, ct)).ToResult(Unknown))
            .WithName("DeleteFolder").Produces<MutationResultDto>().ProducesProblem(StatusCodes.Status404NotFound);
    }
}
