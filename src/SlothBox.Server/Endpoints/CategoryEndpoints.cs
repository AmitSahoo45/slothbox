using SlothBox.Server.Api;

namespace SlothBox.Server.Endpoints;

// Spec 8.2 category routes, mapped by StubEndpoints at their place in the route list.
internal static class CategoryEndpoints
{
    private const string Unknown = "SlothBox has no category with this id.";

    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/categories", async (CategoryService categories, CancellationToken ct) =>
                Results.Ok(await categories.ListAsync(ct)))
            .WithName("GetCategories").Produces<IReadOnlyList<CategoryDto>>();
        api.MapPost("/categories",
                async (CreateCategoryRequest request, CategoryService categories, CancellationToken ct) =>
                    (await categories.CreateAsync(request, ct)).ToResult(Unknown))
            .WithName("CreateCategory").Produces<CategoryDto>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPut("/categories/{id:int}",
                async (int id, UpdateCategoryRequest request, CategoryService categories, CancellationToken ct) =>
                    (await categories.UpdateAsync(id, request, ct)).ToResult(Unknown))
            .WithName("UpdateCategory").Produces<CategoryDto>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        api.MapDelete("/categories/{id:int}", async (int id, CategoryService categories, CancellationToken ct) =>
                (await categories.DeleteAsync(id, ct)).ToResult(Unknown))
            .WithName("DeleteCategory").Produces<MutationResultDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
