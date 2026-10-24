using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Pgvector.EntityFrameworkCore;
using SlothBox.Ai.Analyze;
using SlothBox.Ai.Embed;
using SlothBox.Core;
using SlothBox.Core.Gate;
using SlothBox.Core.Moving;
using SlothBox.Core.Naming;
using SlothBox.Core.Pipeline;
using SlothBox.Core.Reading;
using SlothBox.Data;
using SlothBox.Data.Actions;
using SlothBox.Data.Categories;
using SlothBox.Data.Files;
using SlothBox.Data.Folders;
using SlothBox.Data.Queue;
using SlothBox.Server.Api;
using SlothBox.Server.Events;
using SlothBox.Server.Phone;
using SlothBox.Server.Settings;
using SlothBox.Server.Worker;

namespace SlothBox.Server;

public static class ServerHost
{
    // One client per Ollama caller: the base address is the Ollama URL, and the client's own timeout sits above one
    // Gemma attempt (spec 5.4), so the per-attempt timeouts decide.
    public static HttpClient OllamaClient(OllamaSettings ollama) => new()
    {
        BaseAddress = new Uri(ollama.Url),
        Timeout = TimeSpan.FromSeconds(ollama.AnalyzeTimeoutSeconds + 30)
    };

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
        builder.Services.AddSingleton<ISettingsStore>(_ => new SettingsFile(SettingsFile.DefaultPath));
        builder.Services.AddDbContextFactory<SlothBoxDbContext>((services, options) => options.UseNpgsql(
            DatabaseConnection.For(services.GetRequiredService<ISettingsStore>().Current,
                Environment.GetEnvironmentVariable(DatabaseConnection.PasswordVariable)),
            npgsql => npgsql.UseVector()));
        AddEngine(builder.Services);
        // Test hosts run under "Testing" and must never reach the real database or Ollama.
        if (!builder.Environment.IsEnvironment("Testing"))
            builder.Services.AddHostedService<PipelineWorker>();
        builder.Services.AddSingleton(new PhonePage(EmbeddedUi.Files()));
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
        PhoneEndpoints.Map(app);
        EmbeddedUi.Map(app);
        return app;
    }

    // The pipeline (spec 5.1) and the worker that drives it, over the Data repositories and the Ollama clients.
    private static void AddEngine(IServiceCollection services)
    {
        services.AddSingleton<IJobQueue, JobQueue>();
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<IReceiptRepository, ReceiptRepository>();
        services.AddSingleton<IIgnoredHashes, IgnoredHashes>();
        services.AddSingleton<IActionLog, ActionLog>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<IWatchedFolderRepository, WatchedFolderRepository>();
        services.AddSingleton<Data.Search.SearchService>();
        services.AddSingleton<IFileGate>(sp => new FileGate(sp.GetRequiredService<IWatchedFolderRepository>(),
            sp.GetRequiredService<IIgnoredHashes>(), sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<ISettingsStore>().Current.Pipeline));
        services.AddSingleton<IContentReader>(sp =>
            new ContentReader(sp.GetRequiredService<ISettingsStore>().Current.Pipeline));
        services.AddSingleton<INameBuilder>(sp =>
            new NameBuilder(sp.GetRequiredService<ISettingsStore>().Current.Pipeline));
        services.AddSingleton<ISafeMover>(sp => new SafeMover(sp.GetRequiredService<IActionLog>(),
            sp.GetRequiredService<IFileRepository>(), sp.GetRequiredService<IJobQueue>()));
        services.AddSingleton<IEmbedder>(sp =>
        {
            var ollama = sp.GetRequiredService<ISettingsStore>().Current.Ollama;
            return new Embedder(OllamaClient(ollama), ollama);
        });
        services.AddSingleton<IFileAnalyzer>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStore>().Current;
            return new FileAnalyzer(OllamaClient(settings.Ollama), settings.Ollama, settings.Pipeline);
        });
        services.AddSingleton<IFilePipeline, FilePipeline>();
        services.AddSingleton<IDatabaseStartup, DatabaseStartup>();
        services.AddSingleton<WorkerState>();
        services.AddSingleton<ServerEvents>();
    }
}
