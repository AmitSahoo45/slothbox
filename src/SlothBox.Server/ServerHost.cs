using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using SlothBox.Server.Api;

namespace SlothBox.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 5080);
            options.ListenAnyIP(5081);
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(async (document, context, ct) =>
            {
                document.Info.Title = "SlothBox API";
                document.Info.Version = "v1";
                document.Servers = [];
                Type[] events =
                [
                    typeof(FileFiledEvent), typeof(FileReviewEvent), typeof(FileDuplicateEvent),
                    typeof(FileUndoneEvent), typeof(FileRefiledEvent), typeof(QueueChangedEvent),
                    typeof(StatusChangedEvent), typeof(FolderTidyProgressEvent)
                ];
                foreach (var type in events)
                {
                    var schema = await context.GetOrCreateSchemaAsync(type, null, ct);
                    document.AddComponent(type.Name, schema);
                }
            });
        });
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseRouting();
        app.Use(ApiProtection.InvokeAsync);
        app.UseStatusCodePages();
        app.MapOpenApi();
        StubEndpoints.Map(app);
        EmbeddedUi.Map(app);
        return app;
    }
}
