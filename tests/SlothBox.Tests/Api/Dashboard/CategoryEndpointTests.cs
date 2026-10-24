using System.Net;
using Microsoft.AspNetCore.Mvc;
using SlothBox.Core;
using SlothBox.Server.Api;
using SlothBox.Tests.Data;

namespace SlothBox.Tests.Api.Dashboard;

// The Categories page's API (spec 7.4.7, A22) over a real database.
[Collection(PostgresCollection.Name)]
public sealed class CategoryEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> CreateAsync(DashboardHarness h, FileKind type, string name,
        string description = "Supermarkets and kirana stores", string folder = "") =>
        h.SendAsync(HttpMethod.Post, "/api/categories", new CreateCategoryRequest(type, name, description, folder));

    private static async Task<CategoryDto> CreatedAsync(DashboardHarness h, string name)
    {
        using var response = await CreateAsync(h, FileKind.Receipt, name);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<CategoryDto>();
    }

    [Fact]
    public async Task The_list_has_every_category_with_how_many_files_it_holds()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);

        var categories = await h.GetAsync<List<CategoryDto>>("/api/categories");

        categories.Count.ShouldBe(19);
        categories.Single(c => c.Id == 3).ShouldBe(new CategoryDto(3, FileKind.Receipt, "Bills and recharges",
            "Electricity, mobile, internet, rent, subscriptions", "Bills and recharges", false, 1));
        categories.Single(c => c.Id == 1).FileCount.ShouldBe(0);
    }

    // The plan's E16 check: the prompt uses the live category list.
    [Fact]
    public async Task A_new_category_takes_its_name_as_folder_and_reaches_the_next_gemma_call()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await CreateAsync(h, FileKind.Receipt, " Groceries ");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var created = await response.ReadAsync<CategoryDto>();
        created.ShouldBe(new CategoryDto(created.Id, FileKind.Receipt, "Groceries", "Supermarkets and kirana stores",
            "Groceries", false, 0));
        await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);
        h.Pipeline.Analyzer.Requests.Last().Categories.ShouldContain(c =>
            c.Name == "Groceries" && c.Description == "Supermarkets and kirana stores");
    }

    [Fact]
    public async Task A_name_already_used_for_that_type_is_refused_whatever_its_case()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var taken = await CreateAsync(h, FileKind.Receipt, "food");
        using var otherType = await CreateAsync(h, FileKind.Document, "Food");

        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        otherType.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("other", "Fonts", "Fonts")]
    [InlineData("unknown", "Mystery", "")]
    [InlineData("receipt", "  ", "")]
    [InlineData("receipt", "Bills", "Bills: 2026")]
    [InlineData("receipt", "Bills", "CON")]
    [InlineData("receipt", "Bills", "Bills.")]
    public async Task A_category_the_rules_refuse_is_a_bad_request(string type, string name, string folder)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = await h.SendJsonAsync(HttpMethod.Post, "/api/categories",
            $$"""{"type":"{{type}}","name":"{{name}}","description":"Something","folder":"{{folder}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync<List<CategoryDto>>("/api/categories")).Count.ShouldBe(19);
    }

    [Fact]
    public async Task Editing_changes_the_name_the_description_and_the_folder_while_it_is_empty()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var created = await CreatedAsync(h, "Groceries");

        using var response = await h.SendAsync(HttpMethod.Put, $"/api/categories/{created.Id}",
            new UpdateCategoryRequest("Kirana", "Local stores", "Kirana stores"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.ReadAsync<CategoryDto>()).ShouldBe(created with
        {
            Name = "Kirana", Description = "Local stores", Folder = "Kirana stores"
        });
    }

    [Fact]
    public async Task The_folder_name_is_fixed_once_files_are_in_it()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);

        using var newFolder = await h.SendAsync(HttpMethod.Put, "/api/categories/3",
            new UpdateCategoryRequest("Bills", "Bills of all kinds", "Bills"));
        using var nameOnly = await h.SendAsync(HttpMethod.Put, "/api/categories/3",
            new UpdateCategoryRequest("Bills", "Bills of all kinds", null));

        newFolder.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await newFolder.ReadAsync<ProblemDetails>()).Title.ShouldBe("The folder name is fixed once files are in it.");
        nameOnly.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await nameOnly.ReadAsync<CategoryDto>()).ShouldBe(new CategoryDto(3, FileKind.Receipt, "Bills",
            "Bills of all kinds", "Bills and recharges", false, 1));
    }

    [Fact]
    public async Task Built_in_categories_cannot_be_changed_or_removed()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var edit = await h.SendAsync(HttpMethod.Put, "/api/categories/5",
            new UpdateCategoryRequest("Misc receipts", "Anything", null));
        using var remove = await h.SendAsync(HttpMethod.Delete, "/api/categories/16");

        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        remove.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_category_is_removed_only_while_nothing_is_filed_in_it()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt);
        var created = await CreatedAsync(h, "Groceries");

        using var used = await h.SendAsync(HttpMethod.Delete, "/api/categories/3");
        using var empty = await h.SendAsync(HttpMethod.Delete, $"/api/categories/{created.Id}");

        used.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await used.ReadAsync<ProblemDetails>()).Title.ShouldBe("Move its files to another category first.");
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.GetAsync<List<CategoryDto>>("/api/categories")).ShouldNotContain(c => c.Id == created.Id);
    }

    // A file refiled out of a category still points at it in its history: Undo would put it back there.
    [Fact]
    public async Task A_category_a_file_can_still_be_undone_into_is_kept()
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);
        var created = await CreatedAsync(h, "Groceries");
        var file = await h.FileAsync("IMG_1.jpg", Analyses.JioReceipt with { Category = "Groceries" });
        using var refiled = await h.SendAsync(HttpMethod.Post, $"/api/files/{file.Id}/refile",
            new RefileRequest(FileKind.Receipt, 1, null,
                new ReceiptDto("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi)));
        refiled.StatusCode.ShouldBe(HttpStatusCode.OK, await refiled.Content.ReadAsStringAsync(Ct));

        using var response = await h.SendAsync(HttpMethod.Delete, $"/api/categories/{created.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task An_unknown_category_is_not_found(string method)
    {
        await using var h = await DashboardHarness.CreateAsync(postgres);

        using var response = method == "PUT"
            ? await h.SendAsync(HttpMethod.Put, "/api/categories/999", new UpdateCategoryRequest("A", "B", null))
            : await h.SendAsync(HttpMethod.Delete, "/api/categories/999");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
