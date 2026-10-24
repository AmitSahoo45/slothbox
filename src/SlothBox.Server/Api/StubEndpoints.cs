using Microsoft.OpenApi;

namespace SlothBox.Server.Api;

internal static class StubEndpoints
{
    internal static IResult NotImplemented() => Results.Problem(statusCode: StatusCodes.Status501NotImplemented,
        title: "Not implemented", detail: "This operation is not available in the foundation build.");

    internal static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Dashboard");
        Document<SearchResultDto>(api.MapGet("/search", SearchEndpoints.Search)
            .ProducesProblem(StatusCodes.Status400BadRequest), "Search");
        Endpoints.ReceiptEndpoints.Map(api);
        Endpoints.CategoryEndpoints.Map(api);
        Endpoints.FolderEndpoints.Map(api);
        Document<PickFolderResultDto>(api.MapPost("/system/pick-folder", NotImplemented), "PickFolder");
        Document<SettingsDto>(api.MapGet("/settings", NotImplemented), "GetSettings");
        Document<SettingsDto>(api.MapPut("/settings", (UpdateSettingsRequest request) => NotImplemented()), "UpdateSettings");
        Document<SetupDto>(api.MapGet("/setup", NotImplemented), "GetSetup");
        Document<SetupDto>(api.MapPost("/setup/complete", (CompleteSetupRequest request) => NotImplemented()), "CompleteSetup");
        Document<PhoneDto>(api.MapGet("/phone", NotImplemented), "GetPhone");
        Document<PhoneDto>(api.MapPost("/phone/rotate", NotImplemented), "RotatePhone");
        Document<IReadOnlyList<IgnoredFileDto>>(api.MapGet("/ignored", NotImplemented), "GetIgnored");
        Document<MutationResultDto>(api.MapDelete("/ignored/{sha256}", (string sha256) => NotImplemented()), "AllowIgnored");
        Document<RebuildResultDto>(api.MapPost("/search-data/rebuild", SearchEndpoints.Rebuild), "RebuildSearchData");
    }

    internal static RouteHandlerBuilder Document<T>(RouteHandlerBuilder route, string name) =>
        route.WithName(name).Produces<T>().ProducesProblem(StatusCodes.Status501NotImplemented);

    internal static RouteHandlerBuilder Binary(RouteHandlerBuilder route, string name, string contentType)
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
        return route;
    }
}
