using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlothBox.Data;

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
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

    // Framed, the dashboard's own requests are same-origin and pass the Host and header checks, so another page could
    // disguise its buttons and have Undo since or Tidy them now clicked (clickjacking). No page may frame it.
    [Theory]
    [InlineData("/api/status")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/")]
    [InlineData("/review")]
    [InlineData("/api/files/not-a-file-id/thumb")]
    public async Task No_page_may_frame_the_dashboard(string path)
    {
        using var client = factory.DashboardClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["frame-ancestors 'none'"]);
    }

    // What a browser marks as sent by another site's page (an image, a script, a fetch, a frame, a link or a pop-up),
    // or by a page on another port of this laptop. The dashboard opens from the tray, a bookmark or the address bar.
    [Theory]
    [InlineData("GET", "cross-site", "no-cors", "image")]
    [InlineData("GET", "cross-site", "navigate", "iframe")]
    [InlineData("GET", "cross-site", "navigate", "document")]
    [InlineData("POST", "cross-site", "cors", "empty")]
    [InlineData("GET", "same-site", "no-cors", "script")]
    public async Task Requests_another_site_sends_are_refused(string method, string site, string mode, string dest)
    {
        using var client = factory.DashboardClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/status");
        request.Headers.Add("Sec-Fetch-Site", site);
        request.Headers.Add("Sec-Fetch-Mode", mode);
        request.Headers.Add("Sec-Fetch-Dest", dest);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    // The dashboard's own requests, an address typed or opened from the tray, and clients that send no such header.
    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task The_dashboards_own_requests_are_let_through(string? site)
    {
        using var client = factory.DashboardClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
    public async Task Phone_routes_are_available_only_on_the_phone_listener(string method, string path)
    {
        // The phone listener answers (the synthetic token is wrong, so 401); the dashboard port does not.
        var response = await factory.Server.SendAsync(context =>
        {
            context.Connection.LocalPort = 5081;
            context.Request.Host = new HostString("192.0.2.1", 5081);
            context.Request.Method = method;
            context.Request.Path = path;
            if (method == "POST")
                context.Request.ContentType = "multipart/form-data; boundary=synthetic";
        }, TestContext.Current.CancellationToken);
        response.Response.StatusCode.ShouldBe(401);
        using var client = factory.DashboardClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("", Encoding.UTF8, "multipart/form-data");
        using var dashboard = await client.SendAsync(request, TestContext.Current.CancellationToken);
        dashboard.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Api_tests_never_reach_a_real_database() =>
        Should.Throw<InvalidOperationException>(() => factory.Services
            .GetRequiredService<IDbContextFactory<SlothBoxDbContext>>().CreateDbContext());

    [Fact]
    public async Task File_actions_cannot_address_a_file_by_path()
    {
        using var client = factory.DashboardClient();
        using var response = await client.PostAsync("/api/files/not-a-file-id/open", null,
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
