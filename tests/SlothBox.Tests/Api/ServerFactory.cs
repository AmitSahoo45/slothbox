using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SlothBox.Tests.Api;

public sealed class ServerFactory : WebApplicationFactory<Server.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    public HttpClient DashboardClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:5080"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-SlothBox", "1");
        return client;
    }
}
