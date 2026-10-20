using Microsoft.OpenApi;

namespace SlothBox.Server.Api;

internal static class StubEndpoints
{
    internal static IResult NotImplemented() => Results.Problem(statusCode: StatusCodes.Status501NotImplemented,
        title: "Not implemented", detail: "This operation is not available in the foundation build.");

    internal static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Dashboard");
        Document<StatusDto>(api.MapGet("/status", NotImplemented), "GetStatus");
        Document<StatusDto>(api.MapPost("/pause", NotImplemented), "Pause");
        Document<StatusDto>(api.MapPost("/resume", NotImplemented), "Resume");
        Document<ActivityPageDto>(api.MapGet("/activity", (string? cursor, int? limit) => NotImplemented()), "GetActivity");
        Document<LazinessStatsDto>(api.MapGet("/stats/laziness", (string? range) => NotImplemented()), "GetLazinessStats");
        Document<FileDetailsDto>(api.MapGet("/files/{id:guid}", (Guid id) => NotImplemented()), "GetFile");
        Binary(api.MapGet("/files/{id:guid}/thumb", (Guid id) => NotImplemented()), "GetThumbnail", "image/jpeg");
        Binary(api.MapGet("/files/{id:guid}/preview", (Guid id) => NotImplemented()), "GetPreview", "image/jpeg");
        Document<MutationResultDto>(api.MapPost("/files/{id:guid}/open", (Guid id) => NotImplemented()), "OpenFile");
        Document<MutationResultDto>(api.MapPost("/files/{id:guid}/reveal", (Guid id) => NotImplemented()), "RevealFile");
        Document<UndoResultDto>(api.MapPost("/files/{id:guid}/undo", (Guid id) => NotImplemented()), "UndoFile");
        Document<FileDetailsDto>(api.MapPost("/files/{id:guid}/refile",
            (Guid id, RefileRequest request) => NotImplemented()), "Refile");
        Document<UndoResultDto>(api.MapPost("/undo/last", NotImplemented), "UndoLast");
        Document<UndoBatchResultDto>(api.MapPost("/undo/since", (UndoSinceRequest request) => NotImplemented()), "UndoSince");
        Document<IReadOnlyList<ReviewItemDto>>(api.MapGet("/review", NotImplemented), "GetReview");
        Document<MutationResultDto>(api.MapPost("/review/{id:guid}/skip", (Guid id) => NotImplemented()), "SkipReview");
        Document<SearchResultDto>(api.MapGet("/search",
            (string? q, string? type, int? categoryId, DateOnly? from, DateOnly? to) => NotImplemented()), "Search");
        Document<ReceiptsDto>(api.MapGet("/receipts", (string? month) => NotImplemented()), "GetReceipts");
        Binary(api.MapGet("/receipts/export.csv", (string? month) => NotImplemented()), "ExportReceipts", "text/csv");
        Document<IReadOnlyList<CategoryDto>>(api.MapGet("/categories", NotImplemented), "GetCategories");
        Document<CategoryDto>(api.MapPost("/categories", (CreateCategoryRequest request) => NotImplemented()), "CreateCategory");
        Document<CategoryDto>(api.MapPut("/categories/{id:int}",
            (int id, UpdateCategoryRequest request) => NotImplemented()), "UpdateCategory");
        Document<MutationResultDto>(api.MapDelete("/categories/{id:int}", (int id) => NotImplemented()), "DeleteCategory");
        Document<IReadOnlyList<FolderDto>>(api.MapGet("/folders", NotImplemented), "GetFolders");
        Document<FolderCheckDto>(api.MapPost("/folders/check", (CheckFolderRequest request) => NotImplemented()), "CheckFolder");
        Document<FolderDto>(api.MapPost("/folders", (CreateFolderRequest request) => NotImplemented()), "CreateFolder");
        Document<FolderDto>(api.MapPatch("/folders/{id:int}",
            (int id, UpdateFolderRequest request) => NotImplemented()), "UpdateFolder");
        Document<TidyResultDto>(api.MapPost("/folders/{id:int}/tidy-existing", (int id) => NotImplemented()), "TidyExisting");
        Document<MutationResultDto>(api.MapDelete("/folders/{id:int}", (int id) => NotImplemented()), "DeleteFolder");
        Document<PickFolderResultDto>(api.MapPost("/system/pick-folder", NotImplemented), "PickFolder");
        Document<SettingsDto>(api.MapGet("/settings", NotImplemented), "GetSettings");
        Document<SettingsDto>(api.MapPut("/settings", (UpdateSettingsRequest request) => NotImplemented()), "UpdateSettings");
        Document<SetupDto>(api.MapGet("/setup", NotImplemented), "GetSetup");
        Document<SetupDto>(api.MapPost("/setup/complete", (CompleteSetupRequest request) => NotImplemented()), "CompleteSetup");
        Document<PhoneDto>(api.MapGet("/phone", NotImplemented), "GetPhone");
        Document<PhoneDto>(api.MapPost("/phone/rotate", NotImplemented), "RotatePhone");
        Document<IReadOnlyList<IgnoredFileDto>>(api.MapGet("/ignored", NotImplemented), "GetIgnored");
        Document<MutationResultDto>(api.MapDelete("/ignored/{sha256}", (string sha256) => NotImplemented()), "AllowIgnored");
        Document<RebuildResultDto>(api.MapPost("/search-data/rebuild", NotImplemented), "RebuildSearchData");

        app.MapGet("/events", () => Results.Text(": ready\n\n", "text/event-stream"))
            .WithName("GetEvents").WithTags("Events").Produces<string>(200, "text/event-stream");
        app.MapGet("/u/{token}", (string token) => NotImplemented())
            .WithMetadata(new PhoneEndpoint()).WithName("GetPhonePage").WithTags("Phone")
            .Produces<string>(200, "text/html").ProducesProblem(501);
        Document<PhoneUploadResultDto>(app.MapPost("/u/{token}/upload", (string token) => NotImplemented())
            .WithMetadata(new PhoneEndpoint()).Accepts<PhoneUploadRequest>("multipart/form-data")
            .WithTags("Phone"), "UploadPhoneFile");
    }

    private static void Document<T>(RouteHandlerBuilder route, string name) =>
        route.WithName(name).Produces<T>().ProducesProblem(StatusCodes.Status501NotImplemented);

    private static void Binary(RouteHandlerBuilder route, string name, string contentType)
    {
        route.WithName(name).ProducesProblem(StatusCodes.Status501NotImplemented);
        if (contentType == "text/csv")
            route.Produces<string>(200, contentType);
        else
            route.Produces<byte[]>(200, contentType).AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Responses!["200"].Content![contentType].Schema =
                    new OpenApiSchema { ContentMediaType = contentType };
                return Task.CompletedTask;
            });
    }
}
