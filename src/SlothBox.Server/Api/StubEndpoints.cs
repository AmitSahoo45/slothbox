using Microsoft.OpenApi;

namespace SlothBox.Server.Api;

internal static class StubEndpoints
{
    internal static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Dashboard");
        Document<SearchResultDto>(api.MapGet("/search", SearchEndpoints.Search)
            .ProducesProblem(StatusCodes.Status400BadRequest), "Search");
        Endpoints.ReceiptEndpoints.Map(api);
        Endpoints.CategoryEndpoints.Map(api);
        Endpoints.FolderEndpoints.Map(api);
        Endpoints.SetupEndpoints.Map(api);
        Document<RebuildResultDto>(api.MapPost("/search-data/rebuild", SearchEndpoints.Rebuild), "RebuildSearchData");
    }

    internal static RouteHandlerBuilder Document<T>(RouteHandlerBuilder route, string name) =>
        route.WithName(name).Produces<T>();

    internal static RouteHandlerBuilder Binary(RouteHandlerBuilder route, string name, string contentType)
    {
        route.WithName(name);
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
