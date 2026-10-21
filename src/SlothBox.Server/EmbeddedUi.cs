using Microsoft.Extensions.FileProviders;

namespace SlothBox.Server;

internal static class EmbeddedUi
{
    // The built UI under wwwroot, or no files when the UI has not been built.
    internal static IFileProvider Files()
    {
        var assembly = typeof(ServerHost).Assembly;
        return assembly.GetManifestResourceInfo("Microsoft.Extensions.FileProviders.Embedded.Manifest.xml") is null
            ? new NullFileProvider()
            : new ManifestEmbeddedFileProvider(assembly, "wwwroot");
    }

    internal static void Map(WebApplication app)
    {
        var files = Files();
        if (!files.GetFileInfo("index.html").Exists)
            return;
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapFallback(async context =>
        {
            if (new[] { "/api", "/openapi", "/events", "/u" }
                .Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await Results.Stream(files.GetFileInfo("index.html").CreateReadStream(),
                "text/html; charset=utf-8").ExecuteAsync(context);
        });
    }
}
