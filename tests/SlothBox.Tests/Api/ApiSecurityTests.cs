using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SlothBox.Tests.Api;

public sealed class ApiSecurityTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Theory]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1:5081")]
    [InlineData("localhost")]
    [InlineData("localhost.evil.example:5080")]
    public async Task Rejects_untrusted_dashboard_hosts(string host)
    {
        using var client = factory.DashboardClient();
        client.DefaultRequestHeaders.Host = host;
        using var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("localhost:5080")]
    [InlineData("127.0.0.1:5080")]
    public async Task Accepts_the_two_documented_dashboard_hosts(string host)
    {
        using var client = factory.DashboardClient();
        client.DefaultRequestHeaders.Host = host;
        client.DefaultRequestHeaders.Remove("X-SlothBox");
        using var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Theory]
    [InlineData("POST", "/api/pause")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("PATCH", "/api/folders/1")]
    [InlineData("DELETE", "/api/folders/1")]
    public async Task Every_dashboard_mutation_requires_the_custom_header_before_body_binding(string method, string path)
    {
        using var client = factory.DashboardClient();
        client.DefaultRequestHeaders.Remove("X-SlothBox");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1,1")]
    public async Task A_wrong_or_repeated_header_value_is_not_accepted(string value)
    {
        using var client = factory.DashboardClient();
        client.DefaultRequestHeaders.Remove("X-SlothBox");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-SlothBox", value);
        using var response = await client.PostAsync("/api/pause", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cross_origin_preflight_never_grants_access()
    {
        using var client = factory.DashboardClient();
        client.DefaultRequestHeaders.Remove("X-SlothBox");
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/pause");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "X-SlothBox");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Headers").ShouldBeFalse();
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/events")]
    [InlineData("/")]
    [InlineData("/assets/example.js")]
    public async Task Phone_listener_rejects_dashboard_routes_even_with_a_spoofed_host(string path)
    {
        var response = await factory.Server.SendAsync(context =>
        {
            context.Connection.LocalPort = 5081;
            context.Request.Host = new HostString("127.0.0.1", 5080);
            context.Request.Method = "GET";
            context.Request.Path = path;
        }, TestContext.Current.CancellationToken);
        response.Response.StatusCode.ShouldBe(404);
    }

    [Theory]
    [InlineData("GET", "/u/synthetic-test-token")]
    [InlineData("POST", "/u/synthetic-test-token/upload")]
    public async Task Phone_route_stubs_are_available_only_on_the_phone_listener(string method, string path)
    {
        var response = await factory.Server.SendAsync(context =>
        {
            context.Connection.LocalPort = 5081;
            context.Request.Host = new HostString("192.0.2.1", 5081);
            context.Request.Method = method;
            context.Request.Path = path;
            if (method == "POST")
                context.Request.ContentType = "multipart/form-data; boundary=synthetic";
        }, TestContext.Current.CancellationToken);
        response.Response.StatusCode.ShouldBe(501);
        using var client = factory.DashboardClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("", Encoding.UTF8, "multipart/form-data");
        using var dashboard = await client.SendAsync(request, TestContext.Current.CancellationToken);
        dashboard.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task File_actions_cannot_address_a_file_by_path()
    {
        using var client = factory.DashboardClient();
        using var response = await client.PostAsync("/api/files/not-a-file-id/open", null,
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
