using Microsoft.Extensions.FileProviders;

namespace SlothBox.Server;

internal static class EmbeddedUi
{
    internal static void Map(WebApplication app)
    {
        var assembly = typeof(ServerHost).Assembly;
        if (assembly.GetManifestResourceInfo("Microsoft.Extensions.FileProviders.Embedded.Manifest.xml") is null)
            return;
        var manifest = new ManifestEmbeddedFileProvider(assembly);
        if (!manifest.GetFileInfo("wwwroot/index.html").Exists)
            return;
        var files = new ManifestEmbeddedFileProvider(assembly, "wwwroot");
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
