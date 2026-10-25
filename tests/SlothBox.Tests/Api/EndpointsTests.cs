using System.Net;
using System.Text.Json;

namespace SlothBox.Tests.Api;

public sealed class EndpointsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    // Plan E17: every operation in the contract is built, so none still documents the foundation's 501.
    [Fact]
    public async Task No_operation_documents_a_501()
    {
        using var client = factory.DashboardClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken));
        var stubs = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out var responses) &&
                    responses.TryGetProperty("501", out _))
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"))
            .ToList();
        stubs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Openapi_keeps_optional_request_fields_optional()
    {
        using var client = factory.DashboardClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (schema, optional) in new[]
                 {
                     ("RefileRequest", new[] { "title", "receipt" }),
                     ("CreateFolderRequest", new[] { "label" }),
                     ("UpdateFolderRequest", new[] { "enabled", "mode", "label" })
                 })
        {
            var required = schemas.GetProperty(schema).TryGetProperty("required", out var fields)
                ? fields.EnumerateArray().Select(field => field.GetString()).ToArray() : [];
            foreach (var field in optional)
                required.ShouldNotContain(field, $"{schema}.{field} is optional.");
        }
    }

    [Fact]
    public async Task Status_carries_why_the_worker_is_paused()
    {
        using var client = factory.DashboardClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in new[] { "StatusDto", "StatusChangedEvent" })
        {
            var properties = schemas.GetProperty(schema).GetProperty("properties");
            properties.TryGetProperty("pauseReason", out _).ShouldBeTrue($"{schema}.pauseReason");
            schemas.GetProperty(schema).GetProperty("required").EnumerateArray().Select(x => x.GetString())
                .ShouldNotContain("pauseReason", "pauseReason is null while the worker runs.");
        }
        // The enum is only ever used as a nullable, so the generator lists null among its values.
        schemas.GetProperty("PauseReason").GetProperty("enum").EnumerateArray().Select(x => x.GetString())
            .Where(x => x is not null).ShouldBe(["user", "drive_full", "gemma_offline"]);
    }

    [Fact]
    public async Task The_phone_routes_no_longer_document_a_501()
    {
        using var client = factory.DashboardClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method) in new[] { ("/u/{token}", "get"), ("/u/{token}/upload", "post") })
            paths.GetProperty(path).GetProperty(method).GetProperty("responses").TryGetProperty("501", out _)
                .ShouldBeFalse($"{method.ToUpperInvariant()} {path} is implemented.");
    }

    [Fact]
    public async Task Download_contracts_describe_raw_content_instead_of_base64()
    {
        using var client = factory.DashboardClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, mediaType) in new[]
                 {
                     ("/api/files/{id}/thumb", "image/jpeg"),
                     ("/api/files/{id}/preview", "image/jpeg"),
                     ("/api/receipts/export.csv", "text/csv")
                 })
        {
            var content = paths.GetProperty(path).GetProperty("get").GetProperty("responses")
                .GetProperty("200").GetProperty("content").GetProperty(mediaType);
            if (content.TryGetProperty("schema", out var schema))
            {
                if (schema.TryGetProperty("format", out var format))
                    format.GetString().ShouldNotBe("byte");
                if (schema.TryGetProperty("contentEncoding", out var encoding))
                    encoding.GetString().ShouldNotBe("base64");
            }
        }
    }

    [Fact]
    public async Task Events_has_the_stream_media_type()
    {
        using var client = factory.DashboardClient();
        using var response = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
    }

    [Fact]
    public async Task Openapi_describes_operations_success_models_and_all_named_event_payloads()
    {
        using var client = factory.DashboardClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var operations = root.GetProperty("paths").EnumerateObject()
            .Where(x => x.Name.StartsWith("/api/", StringComparison.Ordinal))
            .Sum(path => path.Value.EnumerateObject().Count(p =>
                new[] { "get", "post", "put", "patch", "delete" }.Contains(p.Name)));
        operations.ShouldBe(41);
        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "FileFiledEvent", "FileReviewEvent", "FileDuplicateEvent", "FileUndoneEvent",
                     "FileRefiledEvent", "QueueChangedEvent", "StatusChangedEvent", "FolderTidyProgressEvent" })
            schemas.TryGetProperty(name, out _).ShouldBeTrue(name);
        root.GetProperty("paths").GetProperty("/api/status").GetProperty("get")
            .GetProperty("responses").TryGetProperty("200", out _).ShouldBeTrue();
        schemas.GetProperty("FileKind").GetProperty("enum").EnumerateArray()
            .Select(x => x.GetString()).ShouldBe(["receipt", "screenshot", "document", "other", "unknown"]);
    }
}
